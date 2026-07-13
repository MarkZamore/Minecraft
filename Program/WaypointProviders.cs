using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed class WaypointProviderRegistry
{
    private static readonly HashSet<string> KnownUnsupportedMapMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "journeymap", "voxelmap", "mapwriter", "antiqueatlas"
    };

    private readonly Logger _logger;
    private readonly IReadOnlyList<IWaypointProvider> _providers =
    [
        new FtbChunksWaypointProvider(),
        new XaeroMinimapWaypointProvider()
    ];

    public WaypointProviderRegistry(Logger logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<(IWaypointProvider Provider, string ModVersion)> Detect(string packDirectory)
    {
        var modVersions = ModMetadataReader.ReadModVersions(Path.Combine(packDirectory, "mods"), _logger);
        foreach (var unsupported in modVersions.Keys.Where(KnownUnsupportedMapMods.Contains))
        {
            _logger.Warn($"Waypoint synchronization is not available for map mod '{unsupported}'. Its local files will be left unchanged.");
        }

        return _providers
            .Where(provider => modVersions.ContainsKey(provider.ModId))
            .Select(provider => (provider, modVersions[provider.ModId]))
            .ToArray();
    }

    public IWaypointProvider? Find(string providerId) =>
        _providers.FirstOrDefault(provider => string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
}

internal static class ModMetadataReader
{
    private const int MaxMetadataBytes = 1024 * 1024;
    private static readonly Regex TomlModsBlock = new(
        @"(?ms)^\s*\[\[mods\]\]\s*(?<body>.*?)(?=^\s*\[\[|\z)",
        RegexOptions.CultureInvariant);
    private static readonly Regex TomlId = new(
        @"(?mi)^\s*modId\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.CultureInvariant);
    private static readonly Regex TomlVersion = new(
        @"(?mi)^\s*version\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.CultureInvariant);

    public static Dictionary<string, string> ReadModVersions(string modsDirectory, Logger logger)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(modsDirectory)) return result;

        foreach (var jarPath in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var archive = ZipFile.OpenRead(jarPath);
                ReadToml(archive.GetEntry("META-INF/neoforge.mods.toml"), result);
                ReadToml(archive.GetEntry("META-INF/mods.toml"), result);
                ReadFabric(archive.GetEntry("fabric.mod.json"), result);
                ReadQuilt(archive.GetEntry("quilt.mod.json"), result);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                logger.Warn($"Could not inspect mod metadata in {Path.GetFileName(jarPath)}: {ex.Message}");
            }
        }

        return result;
    }

    private static void ReadToml(ZipArchiveEntry? entry, Dictionary<string, string> result)
    {
        if (entry is null || entry.Length > MaxMetadataBytes) return;
        var text = ReadEntryText(entry);
        foreach (Match block in TomlModsBlock.Matches(text))
        {
            var body = block.Groups["body"].Value;
            var id = TomlId.Match(body).Groups["value"].Value.Trim();
            var version = TomlVersion.Match(body).Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(id)) result[id] = version;
        }
    }

    private static void ReadFabric(ZipArchiveEntry? entry, Dictionary<string, string> result)
    {
        if (entry is null || entry.Length > MaxMetadataBytes) return;
        using var document = JsonDocument.Parse(ReadEntryText(entry));
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idElement)) return;
        var id = idElement.GetString();
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : string.Empty;
        if (!string.IsNullOrWhiteSpace(id)) result[id] = version ?? string.Empty;
    }

    private static void ReadQuilt(ZipArchiveEntry? entry, Dictionary<string, string> result)
    {
        if (entry is null || entry.Length > MaxMetadataBytes) return;
        using var document = JsonDocument.Parse(ReadEntryText(entry));
        if (!document.RootElement.TryGetProperty("quilt_loader", out var loader)) return;
        var id = loader.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var version = loader.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : string.Empty;
        if (!string.IsNullOrWhiteSpace(id)) result[id] = version ?? string.Empty;
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return reader.ReadToEnd();
    }
}

public sealed class FtbChunksWaypointProvider : IWaypointProvider
{
    private static readonly Regex TeamIdPattern = new(
        @"(?mi)^\s*id\s*:\s*[""']?(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})[""']?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public string ProviderId => "ftb-chunks";
    public string ModId => "ftbchunks";
    public string FormatVersion => "ftb-waypoints-json-1";

    public string? ReadWorldContextId(string worldPath)
    {
        var path = Path.Combine(worldPath, "ftbteams", "ftbteams.snbt");
        if (!File.Exists(path)) return null;
        var match = TeamIdPattern.Match(File.ReadAllText(path));
        return match.Success && Guid.TryParse(match.Groups["id"].Value, out var id)
            ? id.ToString("D")
            : null;
    }

    public IReadOnlyList<string> GetWatchRoots(string gameDirectory) =>
        [Path.Combine(gameDirectory, "local", "ftbchunks", "data")];

    public WaypointSnapshot Export(WaypointNativeContext context)
    {
        var snapshot = CreateSnapshot(context);
        var root = GetNativeRoot(context);
        if (!Directory.Exists(root)) return snapshot;

        foreach (var path in Directory.EnumerateFiles(root, "waypoints.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = WaypointPath.NormalizeRelative(Path.GetRelativePath(root, path));
            var file = new WaypointSnapshotFile { RelativePath = relative, Content = File.ReadAllBytes(path) };
            ValidateFile(file);
            snapshot.Files.Add(file);
        }
        return snapshot;
    }

    public WaypointImportResult Import(
        WaypointNativeContext context,
        WaypointSnapshot snapshot,
        IReadOnlyCollection<string> previousManagedRelativePaths)
    {
        Validate(snapshot);
        var root = GetNativeRoot(context);
        var written = new List<string>();
        foreach (var file in snapshot.Files)
        {
            var destination = WaypointPath.CombineSafe(root, file.RelativePath);
            AtomicFile.WriteAllBytes(destination, file.Content);
            written.Add(WaypointPath.NormalizeRelative(Path.GetRelativePath(context.GameDirectory, destination)));
        }
        WaypointPath.DeleteRemovedManagedFiles(context.GameDirectory, previousManagedRelativePaths, written);
        return new WaypointImportResult(written);
    }

    public void Validate(WaypointSnapshot snapshot)
    {
        WaypointValidation.ValidateEnvelope(snapshot, ProviderId, FormatVersion);
        if (!Guid.TryParse(snapshot.WorldContextId, out _))
        {
            throw new InvalidDataException("FTB Chunks world context is not a UUID.");
        }
        foreach (var file in snapshot.Files) ValidateFile(file);
    }

    private WaypointSnapshot CreateSnapshot(WaypointNativeContext context) => new()
    {
        ProviderId = ProviderId,
        FormatVersion = FormatVersion,
        ModVersion = context.ModVersion,
        WorldContextId = context.WorldContextId
    };

    private static string GetNativeRoot(WaypointNativeContext context) =>
        Path.Combine(context.GameDirectory, "local", "ftbchunks", "data", context.WorldContextId);

    private static void ValidateFile(WaypointSnapshotFile file)
    {
        WaypointValidation.ValidateFile(file, ".json");
        if (!string.Equals(Path.GetFileName(file.RelativePath), "waypoints.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("FTB Chunks snapshot contains a non-waypoint file.");
        }
        using var document = JsonDocument.Parse(file.Content);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("waypoints", out var waypoints) ||
            waypoints.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("FTB Chunks waypoints.json has an unsupported format.");
        }
    }
}

public sealed class XaeroMinimapWaypointProvider : IWaypointProvider
{
    private static readonly Regex WorldIdPattern = new(
        @"(?mi)^\s*id\s*:\s*(?<id>-?\d+)\s*$",
        RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string ProviderId => "xaero-minimap";
    public string ModId => "xaerominimap";
    public string FormatVersion => "xaero-waypoints-text-1";

    public string? ReadWorldContextId(string worldPath)
    {
        var path = Path.Combine(worldPath, "xaeromap.txt");
        if (!File.Exists(path)) return null;
        var match = WorldIdPattern.Match(File.ReadAllText(path));
        return match.Success && int.TryParse(match.Groups["id"].Value, out var id)
            ? id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    public IReadOnlyList<string> GetWatchRoots(string gameDirectory) =>
        [Path.Combine(gameDirectory, "xaero", "minimap")];

    public WaypointSnapshot Export(WaypointNativeContext context)
    {
        var snapshot = new WaypointSnapshot
        {
            ProviderId = ProviderId,
            FormatVersion = FormatVersion,
            ModVersion = context.ModVersion,
            WorldContextId = context.WorldContextId
        };
        var root = GetNativeRoot(context);
        if (!Directory.Exists(root)) return snapshot;
        var nativePrefix = GetNativeFilePrefix(context);

        foreach (var path in Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = WaypointPath.NormalizeRelative(Path.GetRelativePath(root, path));
            if (relative.Split('/').Any(part => part.Equals("backup", StringComparison.OrdinalIgnoreCase) ||
                                                      part.Equals("temp_to_add", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!fileName.Equals(nativePrefix, StringComparison.OrdinalIgnoreCase) &&
                !fileName.StartsWith(nativePrefix + "_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var suffix = fileName[nativePrefix.Length..];
            var directory = Path.GetDirectoryName(relative)?.Replace('\\', '/');
            var canonicalName = $"waypoints{suffix}.txt";
            var canonicalPath = string.IsNullOrWhiteSpace(directory) ? canonicalName : $"{directory}/{canonicalName}";
            var file = new WaypointSnapshotFile { RelativePath = canonicalPath, Content = File.ReadAllBytes(path) };
            ValidateFile(file);
            snapshot.Files.Add(file);
        }
        return snapshot;
    }

    public WaypointImportResult Import(
        WaypointNativeContext context,
        WaypointSnapshot snapshot,
        IReadOnlyCollection<string> previousManagedRelativePaths)
    {
        Validate(snapshot);
        var root = GetNativeRoot(context);
        var prefix = GetNativeFilePrefix(context);
        var written = new List<string>();
        foreach (var file in snapshot.Files)
        {
            var canonicalName = Path.GetFileNameWithoutExtension(file.RelativePath);
            if (!canonicalName.Equals("waypoints", StringComparison.OrdinalIgnoreCase) &&
                !canonicalName.StartsWith("waypoints_", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Xaero snapshot contains an invalid canonical waypoint name.");
            }
            var suffix = canonicalName["waypoints".Length..];
            var directory = Path.GetDirectoryName(file.RelativePath)?.Replace('\\', '/');
            var nativeName = $"{prefix}{suffix}.txt";
            var nativeRelative = string.IsNullOrWhiteSpace(directory) ? nativeName : $"{directory}/{nativeName}";
            var destination = WaypointPath.CombineSafe(root, nativeRelative);
            AtomicFile.WriteAllBytes(destination, file.Content);
            written.Add(WaypointPath.NormalizeRelative(Path.GetRelativePath(context.GameDirectory, destination)));
        }
        WaypointPath.DeleteRemovedManagedFiles(context.GameDirectory, previousManagedRelativePaths, written);
        return new WaypointImportResult(written);
    }

    public void Validate(WaypointSnapshot snapshot)
    {
        WaypointValidation.ValidateEnvelope(snapshot, ProviderId, FormatVersion);
        if (!int.TryParse(snapshot.WorldContextId, out _))
        {
            throw new InvalidDataException("Xaero world context is not an integer.");
        }
        foreach (var file in snapshot.Files) ValidateFile(file);
    }

    private static string GetNativeRoot(WaypointNativeContext context)
    {
        var baseRoot = Path.Combine(context.GameDirectory, "xaero", "minimap");
        if (context.IsHost)
        {
            return Path.Combine(baseRoot, EncodeNode(Path.GetFileName(context.WorldPath)));
        }
        if (string.IsNullOrWhiteSpace(context.RemoteAddress))
        {
            throw new InvalidOperationException("Xaero multiplayer waypoint context has no remote address.");
        }
        return Path.Combine(baseRoot, $"Multiplayer_{EncodeNode(context.RemoteAddress)}");
    }

    private static string GetNativeFilePrefix(WaypointNativeContext context) =>
        context.IsHost ? "waypoints" : $"mw${context.WorldContextId}";

    private static string EncodeNode(string value) => value
        .Replace("_", "%us%", StringComparison.Ordinal)
        .Replace("/", "%fs%", StringComparison.Ordinal)
        .Replace("\\", "%bs%", StringComparison.Ordinal)
        .Replace("[", "%lb%", StringComparison.Ordinal)
        .Replace("]", "%rb%", StringComparison.Ordinal);

    private static void ValidateFile(WaypointSnapshotFile file)
    {
        WaypointValidation.ValidateFile(file, ".txt");
        string text;
        try
        {
            text = StrictUtf8.GetString(file.Content);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Xaero waypoint file is not valid UTF-8.", ex);
        }
        var meaningful = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        if (meaningful.Any(line => !line.StartsWith("waypoint:", StringComparison.OrdinalIgnoreCase) &&
                                   !line.StartsWith("sets:", StringComparison.OrdinalIgnoreCase) &&
                                   !line.StartsWith("slime_chunk_seed:", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Xaero waypoint file has an unsupported record format.");
        }
    }
}

internal static class WaypointValidation
{
    public const long MaxSnapshotBytes = 8L * 1024 * 1024;
    public const int MaxFiles = 512;

    public static void ValidateEnvelope(WaypointSnapshot snapshot, string providerId, string formatVersion)
    {
        if (!string.Equals(snapshot.ProviderId, providerId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.FormatVersion, formatVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported {providerId} waypoint snapshot format.");
        }
        if (snapshot.Files.Count > MaxFiles || snapshot.Files.Sum(file => (long)file.Content.Length) > MaxSnapshotBytes)
        {
            throw new InvalidDataException("Waypoint snapshot exceeds the safe size limit.");
        }
        var duplicates = snapshot.Files.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null) throw new InvalidDataException("Waypoint snapshot contains duplicate paths.");
    }

    public static void ValidateFile(WaypointSnapshotFile file, string requiredExtension)
    {
        _ = WaypointPath.NormalizeRelative(file.RelativePath);
        if (!Path.GetExtension(file.RelativePath).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Waypoint snapshot contains an unexpected file type.");
        }
        if (file.Content.LongLength > MaxSnapshotBytes)
        {
            throw new InvalidDataException("Waypoint file exceeds the safe size limit.");
        }
    }
}

internal static class WaypointPath
{
    public static string NormalizeRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            throw new InvalidDataException("Waypoint path is empty or rooted.");
        }
        var normalized = value.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new InvalidDataException("Waypoint path escapes its storage root.");
        }
        return normalized;
    }

    public static string CombineSafe(string root, string relative)
    {
        relative = NormalizeRelative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Waypoint path escapes its storage root.");
        }
        return full;
    }

    public static void DeleteRemovedManagedFiles(
        string gameDirectory,
        IReadOnlyCollection<string> previousManagedRelativePaths,
        IReadOnlyCollection<string> currentManagedRelativePaths)
    {
        var current = currentManagedRelativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in previousManagedRelativePaths.Where(path => !current.Contains(path)))
        {
            var path = CombineSafe(gameDirectory, relative);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
