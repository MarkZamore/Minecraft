using System.IO;
using System.Text.Json;

namespace Minecraft;

public sealed class WorldMetadataService
{
    public const string MetadataFileName = ".minecraft-portable-world.json";
    public const int CurrentSchemaVersion = 5;
    private const string UnknownBuildName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";
    private const string UnknownOwnerName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public WorldMetadata? Read(string worldPath)
    {
        var metadataPath = GetMetadataPath(worldPath);
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllText(metadataPath), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public WorldMetadata? EnsureMetadata(string worldPath, WorldMetadataContext? context)
    {
        lock (_gate) return EnsureMetadataCore(worldPath, context);
    }

    private WorldMetadata? EnsureMetadataCore(string worldPath, WorldMetadataContext? context)
    {
        var metadataPath = GetMetadataPath(worldPath);
        var existing = Read(worldPath);
        if (existing is not null)
        {
            if (EnsureWorldId(existing))
            {
                try
                {
                    AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(existing, _jsonOptions));
                }
                catch
                {
                    return null;
                }
            }
            return existing;
        }
        if (File.Exists(metadataPath) || context is null)
        {
            return null;
        }

        var metadata = new WorldMetadata
        {
            WorldId = Guid.NewGuid().ToString("D"),
            BuildName = string.IsNullOrWhiteSpace(context.BuildName) ? UnknownBuildName : context.BuildName,
            BuildRelativePath = context.BuildRelativePath,
            PackHash = context.PackHash,
            OwnerIdentityId = string.IsNullOrWhiteSpace(context.OwnerIdentityId) ? "" : context.OwnerIdentityId.Trim(),
            OwnerIdentityName = string.IsNullOrWhiteSpace(context.OwnerIdentityName) ? UnknownOwnerName : context.OwnerIdentityName.Trim(),
            CurrentHolderIdentityId = string.IsNullOrWhiteSpace(context.OwnerIdentityId) ? "" : context.OwnerIdentityId.Trim(),
            CurrentHolderIdentityName = string.IsNullOrWhiteSpace(context.OwnerIdentityName) ? UnknownOwnerName : context.OwnerIdentityName.Trim(),
            SchemaVersion = CurrentSchemaVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MarkedBy = "Minecraft.exe"
        };

        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return metadata;
        }
        catch
        {
            return null;
        }
    }

    public string GetBuildName(string worldPath)
    {
        var metadata = Read(worldPath);
        return string.IsNullOrWhiteSpace(metadata?.BuildName) ? UnknownBuildName : metadata.BuildName;
    }

    public bool TryWriteOwnerMetadata(string worldPath, string? ownerId, string? ownerName, bool overwriteExistingOwner = false)
    {
        lock (_gate) return TryWriteOwnerMetadataCore(worldPath, ownerId, ownerName, overwriteExistingOwner);
    }

    private bool TryWriteOwnerMetadataCore(string worldPath, string? ownerId, string? ownerName, bool overwriteExistingOwner)
    {
        var metadataPath = GetMetadataPath(worldPath);
        WorldMetadata metadata;
        if (File.Exists(metadataPath))
        {
            try
            {
                metadata = JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllText(metadataPath), _jsonOptions) ?? new WorldMetadata();
            }
            catch
            {
                return false;
            }
        }
        else
        {
            metadata = new WorldMetadata
            {
                BuildName = UnknownBuildName,
                BuildRelativePath = string.Empty,
                PackHash = string.Empty,
                SchemaVersion = CurrentSchemaVersion,
                MarkedBy = "Minecraft.exe",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var normalizedOwnerId = string.IsNullOrWhiteSpace(ownerId) ? string.Empty : ownerId.Trim();
        var normalizedOwnerName = string.IsNullOrWhiteSpace(ownerName) ? UnknownOwnerName : ownerName.Trim();

        if (!overwriteExistingOwner &&
            !string.IsNullOrWhiteSpace(metadata.OwnerIdentityId))
        {
            if (string.Equals(metadata.OwnerIdentityId, normalizedOwnerId, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(normalizedOwnerName) &&
                    !string.Equals(metadata.OwnerIdentityName, normalizedOwnerName, StringComparison.Ordinal))
                {
                    metadata.OwnerIdentityName = normalizedOwnerName;
                    try
                    {
                        AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        if (string.Equals(metadata.OwnerIdentityId, normalizedOwnerId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(metadata.OwnerIdentityName, normalizedOwnerName, StringComparison.Ordinal))
        {
            return true;
        }

        metadata.OwnerIdentityId = normalizedOwnerId;
        metadata.OwnerIdentityName = normalizedOwnerName;
        EnsureWorldId(metadata);
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
        }

        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryWriteCurrentHolderMetadata(
        string worldPath,
        string? holderId,
        string? holderName,
        bool transferred)
    {
        lock (_gate) return TryWriteCurrentHolderMetadataCore(worldPath, holderId, holderName, transferred);
    }

    private bool TryWriteCurrentHolderMetadataCore(
        string worldPath,
        string? holderId,
        string? holderName,
        bool transferred)
    {
        var metadataPath = GetMetadataPath(worldPath);
        var metadata = Read(worldPath);
        if (metadata is null)
        {
            if (File.Exists(metadataPath)) return false;
            metadata = new WorldMetadata
            {
                BuildName = UnknownBuildName,
                MarkedBy = "Minecraft.exe",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        metadata.SchemaVersion = CurrentSchemaVersion;
        EnsureWorldId(metadata);
        metadata.CurrentHolderIdentityId = string.IsNullOrWhiteSpace(holderId) ? string.Empty : holderId.Trim();
        metadata.CurrentHolderIdentityName = string.IsNullOrWhiteSpace(holderName) ? UnknownOwnerName : holderName.Trim();
        if (transferred) metadata.LastSuccessfulTransferUtc = DateTimeOffset.UtcNow;
        try
        {
            AtomicFile.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, _jsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetMetadataPath(string worldPath)
    {
        return Path.Combine(worldPath, MetadataFileName);
    }

    public string EnsureWorldId(string worldPath, WorldMetadataContext? context = null)
    {
        lock (_gate)
        {
            var metadata = EnsureMetadataCore(worldPath, context)
                ?? throw new InvalidDataException($"World metadata is missing or damaged: {Path.GetFileName(worldPath)}");
            if (!Guid.TryParse(metadata.WorldId, out var worldId) || worldId == Guid.Empty)
            {
                throw new InvalidDataException($"World metadata has an invalid WorldId: {Path.GetFileName(worldPath)}");
            }
            return worldId.ToString("D");
        }
    }

    private static bool EnsureWorldId(WorldMetadata metadata)
    {
        var changed = false;
        if (!Guid.TryParse(metadata.WorldId, out var worldId) || worldId == Guid.Empty)
        {
            metadata.WorldId = Guid.NewGuid().ToString("D");
            changed = true;
        }
        else
        {
            var normalized = worldId.ToString("D");
            if (!string.Equals(metadata.WorldId, normalized, StringComparison.Ordinal))
            {
                metadata.WorldId = normalized;
                changed = true;
            }
        }
        if (metadata.SchemaVersion < CurrentSchemaVersion)
        {
            metadata.SchemaVersion = CurrentSchemaVersion;
            changed = true;
        }
        return changed;
    }
}
