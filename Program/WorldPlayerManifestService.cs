using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class WorldPlayerManifestService
{
    public const string ManifestFileName = ".minecraft-portable-players.json";
    private const int CurrentSchemaVersion = 2;
    private static readonly string[] ProfileDirectories = ["playerdata", "stats", "advancements"];
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public WorldPlayersManifest Write(string worldPath, LocalIdentityContext? currentHolder)
    {
        var previous = Read(worldPath);
        var previousByMinecraftUuid = previous?.Players
            .GroupBy(player => player.MinecraftUuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, WorldPlayerManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var effectiveHolderUuid = ResolveEffectiveUuid(currentHolder);
        var playerUuids = EnumerateProfileUuids(worldPath).OrderBy(uuid => uuid).ToArray();
        var players = new List<WorldPlayerManifestEntry>(playerUuids.Length);
        foreach (var uuid in playerUuids)
        {
            var minecraftUuid = uuid.ToString("D").ToLowerInvariant();
            previousByMinecraftUuid.TryGetValue(minecraftUuid, out var existing);
            var isCurrentHolder = effectiveHolderUuid == uuid;
            var files = EnumerateProfileFiles(worldPath, uuid)
                .Select(path => CreateFileEntry(worldPath, path))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToList();
            players.Add(new WorldPlayerManifestEntry
            {
                PortableUuid = minecraftUuid,
                MinecraftUuid = minecraftUuid,
                LastKnownName = isCurrentHolder && currentHolder is not null
                    ? currentHolder.IdentityName
                    : existing?.LastKnownName ?? string.Empty,
                Files = files
            });
        }

        var manifest = new WorldPlayersManifest
        {
            SchemaVersion = CurrentSchemaVersion,
            CurrentHolderUuid = currentHolder is null
                ? previous?.CurrentHolderUuid ?? string.Empty
                : effectiveHolderUuid?.ToString("D").ToLowerInvariant() ?? string.Empty,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Players = players
        };
        AtomicFile.WriteAllText(GetManifestPath(worldPath), JsonSerializer.Serialize(manifest, _jsonOptions));
        return manifest;
    }

    public WorldPlayersManifest Validate(string worldPath)
    {
        var manifest = Read(worldPath)
            ?? throw new InvalidDataException($"Player manifest is missing in world {Path.GetFileName(worldPath)}.");
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported player manifest schema {manifest.SchemaVersion}.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.CurrentHolderUuid) &&
            !Guid.TryParse(manifest.CurrentHolderUuid, out _))
        {
            throw new InvalidDataException("Player manifest contains an invalid current holder UUID.");
        }

        var minecraftUuids = new HashSet<Guid>();
        var manifestFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in manifest.Players)
        {
            if (!Guid.TryParse(player.MinecraftUuid, out var minecraftUuid))
            {
                throw new InvalidDataException("Player manifest contains an invalid Minecraft UUID.");
            }
            if (!minecraftUuids.Add(minecraftUuid))
            {
                throw new InvalidDataException($"Player manifest contains duplicate UUID {minecraftUuid:D}.");
            }
            if (!Guid.TryParse(player.PortableUuid, out var portableUuid))
            {
                throw new InvalidDataException("Player manifest contains an invalid portable UUID.");
            }
            if (portableUuid != minecraftUuid)
            {
                throw new InvalidDataException(
                    $"Player manifest maps portable UUID {portableUuid:D} to a different Minecraft UUID {minecraftUuid:D}.");
            }
            foreach (var file in player.Files)
            {
                var path = ResolveManifestPath(worldPath, file.Path);
                var relative = NormalizeRelativePath(Path.GetRelativePath(worldPath, path));
                if (!IsProfilePathForUuid(relative, minecraftUuid))
                {
                    throw new InvalidDataException($"Player manifest file is assigned to the wrong UUID: {file.Path}");
                }
                if (!manifestFiles.Add(relative))
                {
                    throw new InvalidDataException($"Player manifest contains a duplicate file: {file.Path}");
                }
                if (!File.Exists(path)) throw new InvalidDataException($"Player profile file is missing: {file.Path}");
                if (IsPrimaryPlayerDataPath(relative, minecraftUuid))
                {
                    var embeddedUuid = NbtFile.Read(path).Root.GetUuid();
                    if (embeddedUuid != minecraftUuid)
                    {
                        throw new InvalidDataException(
                            $"Player profile {file.Path} contains UUID {embeddedUuid?.ToString("D") ?? "missing"} " +
                            $"instead of {minecraftUuid:D}.");
                    }
                }
                var info = new FileInfo(path);
                if (info.Length != file.Size || !string.Equals(HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Player profile file failed SHA-256 validation: {file.Path}");
                }
            }
        }

        var actualFiles = EnumerateAllProfileFiles(worldPath)
            .Select(path => NormalizeRelativePath(Path.GetRelativePath(worldPath, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualFiles.SetEquals(manifestFiles))
        {
            var missing = actualFiles.Except(manifestFiles, StringComparer.OrdinalIgnoreCase).Take(5);
            var stale = manifestFiles.Except(actualFiles, StringComparer.OrdinalIgnoreCase).Take(5);
            throw new InvalidDataException(
                "Player manifest does not exactly cover the world profile files. " +
                $"Unlisted: {string.Join(", ", missing)}; missing: {string.Join(", ", stale)}");
        }

        return manifest;
    }

    public string HashManifest(string worldPath)
    {
        var path = GetManifestPath(worldPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Player manifest is missing.", path);
        return HashFile(path);
    }

    public WorldPlayersManifest? Read(string worldPath)
    {
        var path = GetManifestPath(worldPath);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<WorldPlayersManifest>(File.ReadAllText(path), _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Player manifest is invalid JSON.", ex);
        }
    }

    private static HashSet<Guid> EnumerateProfileUuids(string worldPath)
    {
        var result = new HashSet<Guid>();
        foreach (var directoryName in ProfileDirectories)
        {
            var directory = Path.Combine(worldPath, directoryName);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var separator = name.IndexOf('.');
                var prefix = separator < 0 ? name : name[..separator];
                if (Guid.TryParse(prefix, out var uuid)) result.Add(uuid);
            }
        }
        return result;
    }

    private static IEnumerable<string> EnumerateProfileFiles(string worldPath, Guid uuid)
    {
        var prefix = uuid.ToString("D").ToLowerInvariant();
        foreach (var directoryName in ProfileDirectories)
        {
            var directory = Path.Combine(worldPath, directoryName);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, prefix + ".*", SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateAllProfileFiles(string worldPath)
    {
        foreach (var directoryName in ProfileDirectories)
        {
            var directory = Path.Combine(worldPath, directoryName);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var separator = name.IndexOf('.');
                var prefix = separator < 0 ? name : name[..separator];
                if (Guid.TryParse(prefix, out _)) yield return path;
            }
        }
    }

    private static bool IsProfilePathForUuid(string relativePath, Guid uuid)
    {
        var normalizedUuid = uuid.ToString("D");
        var separator = relativePath.IndexOf('/');
        if (separator <= 0) return false;
        var directory = relativePath[..separator];
        if (!ProfileDirectories.Contains(directory, StringComparer.OrdinalIgnoreCase)) return false;
        var fileName = relativePath[(separator + 1)..];
        return fileName.StartsWith(normalizedUuid + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrimaryPlayerDataPath(string relativePath, Guid uuid)
    {
        return string.Equals(
            relativePath,
            $"playerdata/{uuid:D}.dat",
            StringComparison.OrdinalIgnoreCase);
    }

    private static WorldPlayerFileManifestEntry CreateFileEntry(string worldPath, string path)
    {
        var info = new FileInfo(path);
        return new WorldPlayerFileManifestEntry
        {
            Path = Path.GetRelativePath(worldPath, path).Replace('\\', '/'),
            Size = info.Length,
            Sha256 = HashFile(path)
        };
    }

    private static string ResolveManifestPath(string worldPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Player manifest contains an invalid file path.");
        }
        var worldRoot = Path.GetFullPath(worldPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(worldRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(worldRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Player manifest path escapes the world directory.");
        }
        return full;
    }

    private static Guid? ResolveEffectiveUuid(LocalIdentityContext? identity)
    {
        if (identity is null) return null;
        if (Guid.TryParse(identity.MinecraftUuid, out var minecraftUuid)) return minecraftUuid;
        return Guid.TryParse(identity.IdentityId, out var identityUuid) ? identityUuid : null;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string GetManifestPath(string worldPath) => Path.Combine(worldPath, ManifestFileName);
}

public sealed class WorldPlayersManifest
{
    public int SchemaVersion { get; set; } = 2;
    public string CurrentHolderUuid { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<WorldPlayerManifestEntry> Players { get; set; } = [];
}

public sealed class WorldPlayerManifestEntry
{
    public string PortableUuid { get; set; } = string.Empty;
    public string MinecraftUuid { get; set; } = string.Empty;
    public string LastKnownName { get; set; } = string.Empty;
    public List<WorldPlayerFileManifestEntry> Files { get; set; } = [];
}

public sealed class WorldPlayerFileManifestEntry
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
