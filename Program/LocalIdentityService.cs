using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace Minecraft;

public sealed class LocalIdentityService
{
    private const int IdentitySchemaVersion = 1;
    private const string LocalSessionTokenNamespace = "MinecraftPortableLocalSession:v1";
    public const int MaxNicknameLength = 16;
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _identityGate = new();
    private PortableIdentity? _identity;

    public LocalIdentityService(AppPaths paths)
    {
        _paths = paths;
    }

    public LocalIdentityContext ResolveContext(AppSettings settings)
    {
        var nickname = NormalizeNickname(settings.PlayerName, Environment.UserName);
        var identityId = LoadOrCreateIdentity().PlayerUuid.ToString("D");

        return new LocalIdentityContext
        {
            IdentityId = identityId,
            IdentityName = nickname,
            MinecraftUuid = identityId,
            SessionAccessToken = CreateLocalSessionToken(identityId, nickname)
        };
    }

    public Guid PlayerUuid => LoadOrCreateIdentity().PlayerUuid;

    private PortableIdentity LoadOrCreateIdentity()
    {
        lock (_identityGate)
        {
            if (_identity is not null) return _identity;
            Directory.CreateDirectory(_paths.Personal);
            if (File.Exists(_paths.IdentityFile))
            {
                _identity = ReadIdentity(_paths.IdentityFile);
                return _identity;
            }

            var created = new PortableIdentity
            {
                SchemaVersion = IdentitySchemaVersion,
                PlayerUuid = Guid.NewGuid(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            var temporaryPath = Path.Combine(
                _paths.Personal,
                $".{Path.GetFileName(_paths.IdentityFile)}.{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(created, _jsonOptions));
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                var verified = ReadIdentity(temporaryPath);
                if (verified.PlayerUuid != created.PlayerUuid || verified.CreatedAtUtc != created.CreatedAtUtc)
                {
                    throw new InvalidDataException("New portable identity failed verification.");
                }
                try
                {
                    File.Move(temporaryPath, _paths.IdentityFile, overwrite: false);
                    _identity = created;
                }
                catch (IOException) when (File.Exists(_paths.IdentityFile))
                {
                    _identity = ReadIdentity(_paths.IdentityFile);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            return _identity ?? throw new InvalidOperationException("Portable identity could not be created.");
        }
    }

    private PortableIdentity ReadIdentity(string path)
    {
        try
        {
            var identity = JsonSerializer.Deserialize<PortableIdentity>(File.ReadAllText(path), _jsonOptions);
            if (identity is null ||
                identity.SchemaVersion != IdentitySchemaVersion ||
                identity.PlayerUuid == Guid.Empty ||
                identity.CreatedAtUtc == default)
            {
                throw new InvalidDataException("UUID.json has an invalid schema or identity value.");
            }

            return identity;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new InvalidDataException(
                "Minecraft\\Personal\\UUID.json is damaged or unreadable. Restore the original file before starting Minecraft.",
                ex);
        }
    }

    public static string NormalizeNickname(string? value, string? fallback = null)
    {
        if (TryNormalizeNickname(value, out var normalized, out _))
        {
            return normalized;
        }

        return TryNormalizeNickname(fallback, out normalized, out _) ? normalized : "Player";
    }

    public static bool IsNicknameDraftValid(string? value)
    {
        if (value is null || value.Length > MaxNicknameLength)
        {
            return false;
        }

        return HasOnlyAllowedUnicode(value);
    }

    public static bool TryNormalizeNickname(string? value, out string normalized, out string error)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            error = "Ник не может быть пустым.";
            return false;
        }
        if (normalized.Length > MaxNicknameLength)
        {
            error = $"Ник не может быть длиннее {MaxNicknameLength} символов UTF-16.";
            return false;
        }
        if (!HasOnlyAllowedUnicode(normalized))
        {
            error = "Ник содержит управляющий символ или перенос строки.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool HasOnlyAllowedUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }
                index++;
                continue;
            }
            if (char.IsLowSurrogate(character) || char.IsControl(character))
            {
                return false;
            }

            var category = char.GetUnicodeCategory(character);
            if (category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
            if (category == UnicodeCategory.Format && character is not ('\u200C' or '\u200D'))
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateLocalSessionToken(string identityId, string nickname)
    {
        var seed = $"{LocalSessionTokenNamespace}|{identityId}|{nickname}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash).Substring(0, 40).ToLowerInvariant();
    }
}

public sealed class PortableIdentity
{
    public int SchemaVersion { get; set; } = 1;
    public Guid PlayerUuid { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class LocalIdentityContext
{
    public string IdentityId { get; set; } = "";
    public string IdentityName { get; set; } = "";
    public string MinecraftUuid { get; set; } = "";
    public string SessionAccessToken { get; set; } = "";
}
