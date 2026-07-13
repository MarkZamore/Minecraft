using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const long DefaultMaxArchiveBytes = 10L * 1024 * 1024 * 1024;
    private const double MinVoiceMasterVolume = 0d;
    private const double MaxVoiceMasterVolume = 2d;
    private const string DefaultVoicePttMode = "Off";
    private const string DefaultVoicePushToTalkBinding = "Key:V";

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
    }

    public AppSettings Load()
    {
        var settingsFile = ResolveSettingsFileToRead();

        if (!File.Exists(settingsFile))
        {
            var defaults = CreateSafeDefaults();
            TryPersistSafeDefaults(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(settingsFile);
            var hasConfiguredMemory = HasJsonProperty(json, "maxMemoryGb");
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _options) ?? new AppSettings();
            ApplyNetworkToolMigration(settings, json);
            settings = ApplyFallbacks(settings, useRecommendedMemory: !hasConfiguredMemory);
            TryPersistSafeDefaults(settings);
            return settings;
        }
        catch
        {
            var defaults = CreateSafeDefaults();
            TryPersistSafeDefaults(defaults);
            return defaults;
        }
    }

    public void Save(AppSettings settings)
    {
        settings = ApplyFallbacks(settings);
        AtomicFile.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(settings, _options));
    }

    private string ResolveSettingsFileToRead()
    {
        if (File.Exists(_paths.SettingsFile))
        {
            return _paths.SettingsFile;
        }

        return _paths.LegacySettingsFiles.FirstOrDefault(File.Exists) ?? _paths.SettingsFile;
    }

    private static AppSettings ApplyFallbacks(AppSettings? source, bool useRecommendedMemory = false)
    {
        var settings = source ?? new AppSettings();

        settings.PlayerName = settings.PlayerName?.Trim() ?? "";
        settings.PreviousPlayerName = settings.PreviousPlayerName?.Trim() ?? "";

        settings.LocalIdentityId = settings.LocalIdentityId?.Trim() ?? "";
        settings.LocalIdentityName = settings.LocalIdentityName?.Trim() ?? "";
        if (source is null || useRecommendedMemory ||
            settings.MaxMemoryGb < MemorySizingService.MinMemoryGb ||
            settings.MaxMemoryGb > MemorySizingService.MaxMemoryGb)
        {
            settings.MaxMemoryGb = MemorySizingService.GetRecommendedDefaultMemoryGb();
        }
        else
        {
            settings.MaxMemoryGb = Math.Clamp(
                settings.MaxMemoryGb,
                MemorySizingService.MinMemoryGb,
                MemorySizingService.MaxMemoryGb);
        }

        if (settings.MaxArchiveBytes <= 0)
        {
            settings.MaxArchiveBytes = DefaultMaxArchiveBytes;
        }

        settings.ClientRelativePath = settings.ClientRelativePath?.Trim() ?? "";
        settings.NetworkName = settings.NetworkName?.Trim() ?? "";
        settings.NetworkPassword = settings.NetworkPassword?.Trim() ?? "";
        settings.NetworkToolId = string.IsNullOrWhiteSpace(settings.NetworkToolId)
            ? "hamachi"
            : settings.NetworkToolId.Trim().ToLowerInvariant();
        settings.SkinPath = settings.SkinPath?.Trim() ?? "";
        settings.SelectedWorldRelativePath = settings.SelectedWorldRelativePath?.Trim() ?? "";
        settings.VoiceInputDeviceId = settings.VoiceInputDeviceId?.Trim() ?? "";
        settings.VoiceOutputDeviceId = settings.VoiceOutputDeviceId?.Trim() ?? "";
        settings.VoicePushToTalkKey = string.IsNullOrWhiteSpace(settings.VoicePushToTalkKey)
            ? "V"
            : settings.VoicePushToTalkKey.Trim();
        settings.VoiceMasterVolume = Math.Clamp(settings.VoiceMasterVolume, MinVoiceMasterVolume, MaxVoiceMasterVolume);
        settings.VoicePttMode = NormalizePttMode(settings.VoicePttMode);
        settings.VoicePushToTalkBinding = NormalizePttBinding(settings.VoicePushToTalkBinding, settings.VoicePushToTalkKey);
        settings.VoiceInputVolume = Math.Clamp(settings.VoiceInputVolume, MinVoiceMasterVolume, MaxVoiceMasterVolume);
        settings.VoiceOutputVolume = Math.Clamp(settings.VoiceOutputVolume, MinVoiceMasterVolume, MaxVoiceMasterVolume);

        return settings;
    }

    private static string NormalizePttMode(string? value)
    {
        var mode = value?.Trim();
        return mode is "Off" or "Hold" or "Toggle" ? mode : DefaultVoicePttMode;
    }

    private static string NormalizePttBinding(string? value, string? legacyKey)
    {
        var binding = value?.Trim();
        if (!string.IsNullOrWhiteSpace(binding) &&
            (binding.StartsWith("Key:", StringComparison.OrdinalIgnoreCase) ||
             binding.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase)))
        {
            return binding;
        }

        var key = string.IsNullOrWhiteSpace(legacyKey) ? "V" : legacyKey.Trim();
        return string.IsNullOrWhiteSpace(key) ? DefaultVoicePushToTalkBinding : $"Key:{key}";
    }

    private static AppSettings CreateSafeDefaults()
    {
        return ApplyFallbacks(new AppSettings(), useRecommendedMemory: true);
    }

    private static bool HasJsonProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object &&
               document.RootElement.EnumerateObject().Any(property =>
                   string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyNetworkToolMigration(AppSettings settings, string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            settings.NetworkName = "";
            settings.NetworkPassword = "";
            settings.NetworkToolId = "hamachi";
            return;
        }

        var root = document.RootElement;
        if (!TryGetPropertyIgnoreCase(root, "networkToolAutoLaunch", out _) &&
            TryGetPropertyIgnoreCase(root, "radminAutoLaunch", out var legacyAutoLaunch) &&
            legacyAutoLaunch.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            settings.NetworkToolAutoLaunch = legacyAutoLaunch.GetBoolean();
        }

        var alreadyMigrated = TryGetPropertyIgnoreCase(root, "networkToolId", out var toolId) &&
                              toolId.ValueKind == JsonValueKind.String &&
                              string.Equals(toolId.GetString(), "hamachi", StringComparison.OrdinalIgnoreCase);
        if (!alreadyMigrated)
        {
            settings.NetworkName = "";
            settings.NetworkPassword = "";
        }
        settings.NetworkToolId = "hamachi";
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private void TryPersistSafeDefaults(AppSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch
        {
            // Settings persistence is optional on startup.
        }
    }
}
