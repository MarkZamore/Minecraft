using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class WaypointStoreService
{
    public const string StoreDirectoryName = ".minecraft-portable-waypoints";
    public const string ManifestFileName = "manifest.json";
    public const int SchemaVersion = 1;

    private readonly WorldMetadataService _worldMetadata;
    private readonly WaypointProviderRegistry _providers;
    private readonly Logger _logger;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public WaypointStoreService(
        WorldMetadataService worldMetadata,
        WaypointProviderRegistry providers,
        Logger logger)
    {
        _worldMetadata = worldMetadata;
        _providers = providers;
        _logger = logger;
    }

    public WaypointWorldManifest EnsureManifest(string worldPath)
    {
        lock (_gate)
        {
            var worldId = _worldMetadata.EnsureWorldId(worldPath);
            var manifestPath = GetManifestPath(worldPath);
            if (File.Exists(manifestPath))
            {
                var existing = ReadManifest(manifestPath);
                if (!string.Equals(existing.WorldId, worldId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Waypoint manifest belongs to a different world.");
                }
                ValidateManifestCore(worldPath, existing, validateProviderFormats: true);
                return existing;
            }

            var manifest = new WaypointWorldManifest
            {
                SchemaVersion = SchemaVersion,
                WorldId = worldId,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            WriteManifest(worldPath, manifest);
            return manifest;
        }
    }

    public void ValidateManifest(string worldPath)
    {
        lock (_gate)
        {
            var manifestPath = GetManifestPath(worldPath);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException("World waypoint manifest is missing.");
            }
            var manifest = ReadManifest(manifestPath);
            var metadata = _worldMetadata.Read(worldPath)
                ?? throw new InvalidDataException("World metadata is missing while validating waypoints.");
            if (!Guid.TryParse(metadata.WorldId, out var metadataId) ||
                !Guid.TryParse(manifest.WorldId, out var manifestId) ||
                metadataId != manifestId)
            {
                throw new InvalidDataException("World and waypoint manifest identifiers do not match.");
            }
            ValidateManifestCore(worldPath, manifest, validateProviderFormats: true);
        }
    }

    public string GetManifestHash(string worldPath)
    {
        ValidateManifest(worldPath);
        return HashFile(GetManifestPath(worldPath));
    }

    public WaypointStoredSnapshot? ReadSnapshot(string worldPath, string playerUuid, string providerId)
    {
        lock (_gate)
        {
            var manifest = EnsureManifest(worldPath);
            var normalizedUuid = NormalizeUuid(playerUuid);
            if (!manifest.Players.TryGetValue(normalizedUuid, out var player) ||
                !player.Providers.TryGetValue(providerId, out var providerManifest))
            {
                return null;
            }

            var snapshot = ReadSnapshotCore(worldPath, providerManifest);
            return new WaypointStoredSnapshot(snapshot, providerManifest.Revision, providerManifest.Sha256);
        }
    }

    public WaypointSaveResult SaveLocalSnapshot(
        string worldPath,
        string playerUuid,
        string playerName,
        WaypointSnapshot snapshot)
    {
        return SaveSnapshot(worldPath, playerUuid, playerName, snapshot, expectedRevision: null, expectedSha256: null);
    }

    public WaypointSaveResult SaveIncomingSnapshot(
        string worldPath,
        string playerUuid,
        string playerName,
        WaypointSnapshot snapshot,
        long expectedRevision,
        string expectedSha256)
    {
        return SaveSnapshot(worldPath, playerUuid, playerName, snapshot, expectedRevision, expectedSha256);
    }

    private WaypointSaveResult SaveSnapshot(
        string worldPath,
        string playerUuid,
        string playerName,
        WaypointSnapshot snapshot,
        long? expectedRevision,
        string? expectedSha256)
    {
        lock (_gate)
        {
            var provider = _providers.Find(snapshot.ProviderId)
                ?? throw new InvalidDataException($"Waypoint provider '{snapshot.ProviderId}' is not supported by this application.");
            provider.Validate(snapshot);
            var normalizedUuid = NormalizeUuid(playerUuid);
            var manifest = EnsureManifest(worldPath);
            if (!manifest.Players.TryGetValue(normalizedUuid, out var player))
            {
                player = new WaypointPlayerManifest { PlayerUuid = normalizedUuid };
                manifest.Players[normalizedUuid] = player;
            }
            player.LastKnownName = playerName?.Trim() ?? string.Empty;
            player.Providers.TryGetValue(snapshot.ProviderId, out var current);

            if (expectedRevision.HasValue)
            {
                var currentRevision = current?.Revision ?? 0;
                var currentHash = current?.Sha256 ?? string.Empty;
                if (currentRevision != expectedRevision.Value ||
                    !string.Equals(currentHash, expectedSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    return new WaypointSaveResult(false, true, currentRevision, currentHash, "Waypoint snapshot is based on an older revision.");
                }
            }

            var snapshotHash = ComputeSnapshotHash(snapshot);
            if (current is not null && string.Equals(current.Sha256, snapshotHash, StringComparison.OrdinalIgnoreCase))
            {
                current.ModVersion = snapshot.ModVersion;
                current.SavedAtUtc = DateTimeOffset.UtcNow;
                manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
                WriteManifest(worldPath, manifest);
                return new WaypointSaveResult(true, false, current.Revision, current.Sha256, "unchanged");
            }

            var revision = checked((current?.Revision ?? 0) + 1);
            var safeProviderId = NormalizeProviderId(snapshot.ProviderId);
            var revisionDirectory = WaypointPath.NormalizeRelative(
                $"players/{normalizedUuid}/{safeProviderId}/revisions/{revision:D20}-{snapshotHash[..12]}");
            var revisionRoot = WaypointPath.CombineSafe(GetStoreRoot(worldPath), revisionDirectory);
            if (Directory.Exists(revisionRoot)) Directory.Delete(revisionRoot, recursive: true);
            Directory.CreateDirectory(revisionRoot);

            var storedFiles = new List<WaypointStoredFile>();
            try
            {
                foreach (var file in snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
                {
                    var relative = WaypointPath.NormalizeRelative(file.RelativePath);
                    var destination = WaypointPath.CombineSafe(revisionRoot, relative);
                    AtomicFile.WriteAllBytes(destination, file.Content);
                    var fileHash = HashFile(destination);
                    storedFiles.Add(new WaypointStoredFile
                    {
                        RelativePath = relative,
                        Sha256 = fileHash,
                        SizeBytes = file.Content.LongLength
                    });
                }

                var providerManifest = new WaypointProviderManifest
                {
                    ProviderId = snapshot.ProviderId,
                    FormatVersion = snapshot.FormatVersion,
                    ModVersion = snapshot.ModVersion,
                    WorldContextId = snapshot.WorldContextId,
                    Revision = revision,
                    Sha256 = snapshotHash,
                    SizeBytes = snapshot.Files.Sum(file => (long)file.Content.Length),
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    RevisionDirectory = revisionDirectory,
                    Files = storedFiles
                };
                player.Providers[snapshot.ProviderId] = providerManifest;
                manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;
                ValidateProviderFiles(
                    worldPath,
                    normalizedUuid,
                    snapshot.ProviderId,
                    providerManifest,
                    validateProviderFormat: true);
                WriteManifest(worldPath, manifest);

                if (current is not null && !string.Equals(current.RevisionDirectory, revisionDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteRevisionBestEffort(worldPath, current.RevisionDirectory);
                }
                return new WaypointSaveResult(true, false, revision, snapshotHash, "saved");
            }
            catch
            {
                if (Directory.Exists(revisionRoot)) Directory.Delete(revisionRoot, recursive: true);
                throw;
            }
        }
    }

    private WaypointWorldManifest ReadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<WaypointWorldManifest>(File.ReadAllText(path), _jsonOptions)
            ?? throw new InvalidDataException("Waypoint manifest is empty.");
        if (manifest.SchemaVersion != SchemaVersion || !Guid.TryParse(manifest.WorldId, out _))
        {
            throw new InvalidDataException("Waypoint manifest has an unsupported schema.");
        }
        manifest.Players = new Dictionary<string, WaypointPlayerManifest>(
            manifest.Players ?? new Dictionary<string, WaypointPlayerManifest>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var player in manifest.Players.Values)
        {
            if (player is null) throw new InvalidDataException("Waypoint manifest contains an empty player entry.");
            player.Providers = new Dictionary<string, WaypointProviderManifest>(
                player.Providers ?? new Dictionary<string, WaypointProviderManifest>(),
                StringComparer.OrdinalIgnoreCase);
        }
        return manifest;
    }

    private void WriteManifest(string worldPath, WaypointWorldManifest manifest)
    {
        manifest.SchemaVersion = SchemaVersion;
        AtomicFile.WriteAllText(GetManifestPath(worldPath), JsonSerializer.Serialize(manifest, _jsonOptions));
        var verified = ReadManifest(GetManifestPath(worldPath));
        ValidateManifestCore(worldPath, verified, validateProviderFormats: false);
    }

    private void ValidateManifestCore(string worldPath, WaypointWorldManifest manifest, bool validateProviderFormats)
    {
        foreach (var (playerUuid, player) in manifest.Players)
        {
            if (!string.Equals(playerUuid, NormalizeUuid(player.PlayerUuid), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Waypoint manifest player key does not match its UUID.");
            }
            foreach (var (providerKey, provider) in player.Providers)
            {
                if (provider is null) throw new InvalidDataException("Waypoint manifest contains an empty provider entry.");
                ValidateProviderFiles(worldPath, playerUuid, providerKey, provider, validateProviderFormats);
            }
        }
    }

    private void ValidateProviderFiles(
        string worldPath,
        string playerUuid,
        string providerKey,
        WaypointProviderManifest manifest,
        bool validateProviderFormat)
    {
        var normalizedUuid = NormalizeUuid(playerUuid);
        var normalizedProviderId = NormalizeProviderId(manifest.ProviderId);
        if (!string.Equals(providerKey, manifest.ProviderId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(normalizedProviderId, manifest.ProviderId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Waypoint provider key does not match its manifest ID.");
        }
        if (manifest.Revision <= 0 || string.IsNullOrWhiteSpace(manifest.Sha256) ||
            string.IsNullOrWhiteSpace(manifest.RevisionDirectory))
        {
            throw new InvalidDataException("Waypoint provider manifest is incomplete.");
        }
        if (manifest.Sha256.Length != 64 || manifest.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Waypoint provider manifest has an invalid SHA-256 value.");
        }
        var revisionDirectory = WaypointPath.NormalizeRelative(manifest.RevisionDirectory);
        var expectedPrefix = $"players/{normalizedUuid}/{normalizedProviderId}/revisions/";
        if (!revisionDirectory.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Waypoint provider revision points outside its player directory.");
        }
        manifest.Files ??= [];
        if (manifest.Files.Count > WaypointValidation.MaxFiles ||
            manifest.SizeBytes < 0 || manifest.SizeBytes > WaypointValidation.MaxSnapshotBytes ||
            manifest.Files.Sum(file => file?.SizeBytes ?? -1) != manifest.SizeBytes)
        {
            throw new InvalidDataException("Waypoint provider manifest exceeds the safe size limit.");
        }
        if (manifest.Files.Any(file => file is null) ||
            manifest.Files.GroupBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Waypoint provider manifest contains invalid or duplicate file entries.");
        }
        var snapshot = ReadSnapshotCore(worldPath, manifest);
        var calculatedHash = ComputeSnapshotHash(snapshot);
        if (!string.Equals(calculatedHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase) ||
            snapshot.Files.Sum(file => (long)file.Content.Length) != manifest.SizeBytes)
        {
            throw new InvalidDataException("Waypoint provider snapshot hash or size does not match its manifest.");
        }

        var provider = _providers.Find(manifest.ProviderId);
        if (provider is not null && validateProviderFormat)
        {
            provider.Validate(snapshot);
        }
        else if (provider is null && validateProviderFormat)
        {
            _logger.Warn($"Waypoint data for unsupported provider '{manifest.ProviderId}' was preserved without format conversion.");
        }
    }

    private WaypointSnapshot ReadSnapshotCore(string worldPath, WaypointProviderManifest manifest)
    {
        var revisionRoot = WaypointPath.CombineSafe(GetStoreRoot(worldPath), manifest.RevisionDirectory);
        var snapshot = new WaypointSnapshot
        {
            ProviderId = manifest.ProviderId,
            FormatVersion = manifest.FormatVersion,
            ModVersion = manifest.ModVersion,
            WorldContextId = manifest.WorldContextId
        };
        foreach (var stored in manifest.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            var path = WaypointPath.CombineSafe(revisionRoot, stored.RelativePath);
            if (!File.Exists(path)) throw new InvalidDataException("Waypoint snapshot file is missing.");
            var bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != stored.SizeBytes ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), stored.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Waypoint snapshot file failed hash validation.");
            }
            snapshot.Files.Add(new WaypointSnapshotFile { RelativePath = stored.RelativePath, Content = bytes });
        }
        return snapshot;
    }

    public static string ComputeSnapshotHash(WaypointSnapshot snapshot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, snapshot.ProviderId);
        AppendString(hash, snapshot.FormatVersion);
        AppendString(hash, snapshot.WorldContextId);
        foreach (var file in snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            AppendString(hash, WaypointPath.NormalizeRelative(file.RelativePath));
            AppendInt64(hash, file.Content.LongLength);
            hash.AppendData(file.Content);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        AppendInt64(hash, bytes.LongLength);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static string NormalizeUuid(string value)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
        {
            throw new InvalidDataException("Waypoint player UUID is invalid.");
        }
        return id.ToString("D");
    }

    private static string NormalizeProviderId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("Waypoint provider ID contains unsafe characters.");
        }
        return value.ToLowerInvariant();
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string GetStoreRoot(string worldPath) => Path.Combine(worldPath, StoreDirectoryName);
    private static string GetManifestPath(string worldPath) => Path.Combine(GetStoreRoot(worldPath), ManifestFileName);

    private static void DeleteRevisionBestEffort(string worldPath, string revisionDirectory)
    {
        try
        {
            var path = WaypointPath.CombineSafe(GetStoreRoot(worldPath), revisionDirectory);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

public sealed record WaypointStoredSnapshot(WaypointSnapshot Snapshot, long Revision, string Sha256);
public sealed record WaypointSaveResult(bool Saved, bool Conflict, long Revision, string Sha256, string Message);
