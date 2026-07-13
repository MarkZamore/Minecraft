namespace Minecraft;

public sealed class WaypointSnapshot
{
    public string ProviderId { get; set; } = "";
    public string FormatVersion { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string WorldContextId { get; set; } = "";
    public List<WaypointSnapshotFile> Files { get; set; } = [];
}

public sealed class WaypointSnapshotFile
{
    public string RelativePath { get; set; } = "";
    public byte[] Content { get; set; } = [];
}

public sealed record WaypointNativeContext(
    string GameDirectory,
    string WorldPath,
    string WorldId,
    string WorldContextId,
    string ModVersion,
    bool IsHost,
    string? RemoteAddress);

public sealed record WaypointImportResult(IReadOnlyList<string> ManagedRelativePaths);

public interface IWaypointProvider
{
    string ProviderId { get; }
    string ModId { get; }
    string FormatVersion { get; }
    string? ReadWorldContextId(string worldPath);
    IReadOnlyList<string> GetWatchRoots(string gameDirectory);
    WaypointSnapshot Export(WaypointNativeContext context);
    WaypointImportResult Import(
        WaypointNativeContext context,
        WaypointSnapshot snapshot,
        IReadOnlyCollection<string> previousManagedRelativePaths);
    void Validate(WaypointSnapshot snapshot);
}

public sealed class WaypointWorldManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string WorldId { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, WaypointPlayerManifest> Players { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WaypointPlayerManifest
{
    public string PlayerUuid { get; set; } = "";
    public string LastKnownName { get; set; } = "";
    public Dictionary<string, WaypointProviderManifest> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WaypointProviderManifest
{
    public string ProviderId { get; set; } = "";
    public string FormatVersion { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string WorldContextId { get; set; } = "";
    public long Revision { get; set; }
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset SavedAtUtc { get; set; }
    public string RevisionDirectory { get; set; } = "";
    public List<WaypointStoredFile> Files { get; set; } = [];
}

public sealed class WaypointStoredFile
{
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed class WaypointSyncEnvelope
{
    public string Protocol { get; set; } = WaypointSyncService.ProtocolName;
    public int ProtocolVersion { get; set; } = WaypointSyncService.ProtocolVersion;
    public string MessageType { get; set; } = "";
    public string WorldId { get; set; } = "";
    public string PlayerUuid { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string WorldContextId { get; set; } = "";
    public long BaseRevision { get; set; }
    public string BaseSha256 { get; set; } = "";
    public WaypointSnapshot? Snapshot { get; set; }
}

public sealed class WaypointSyncReply
{
    public string Protocol { get; set; } = WaypointSyncService.ProtocolName;
    public int ProtocolVersion { get; set; } = WaypointSyncService.ProtocolVersion;
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public long Revision { get; set; }
    public string Sha256 { get; set; } = "";
    public WaypointSnapshot? Snapshot { get; set; }
}
