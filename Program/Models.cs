using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Minecraft;

public sealed class AppSettings
{
    public string PlayerName { get; set; } = "";
    public string PreviousPlayerName { get; set; } = "";

    [JsonIgnore]
    public string LocalIdentityId { get; set; } = "";

    [JsonIgnore]
    public string LocalIdentityName { get; set; } = "";

    public string? AdapterId { get; set; }

    public int MaxMemoryGb { get; set; } = 16;

    [JsonIgnore]
    public long MaxArchiveBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public string ClientRelativePath { get; set; } = "";
    public string RadminNetworkName { get; set; } = "";
    public string RadminNetworkPassword { get; set; } = "";
    public bool RadminAutoLaunch { get; set; } = true;
    public string SkinPath { get; set; } = "";
    public string SelectedWorldRelativePath { get; set; } = "";
    public string VoiceInputDeviceId { get; set; } = "";
    public string VoiceOutputDeviceId { get; set; } = "";

    [JsonIgnore]
    public bool VoiceMuted { get; set; }

    [JsonIgnore]
    public bool VoiceDeafened { get; set; }

    [JsonIgnore]
    public double VoiceMasterVolume { get; set; } = 1.0;

    [JsonIgnore]
    public string VoicePushToTalkKey { get; set; } = "V";

    public string VoicePttMode { get; set; } = "Off";
    public string VoicePushToTalkBinding { get; set; } = "Key:V";
    public double VoiceInputVolume { get; set; } = 1.0;
    public double VoiceOutputVolume { get; set; } = 1.0;
}

public sealed class NetworkAdapterInfo
{
    public required string Id { get; init; }
    public int InterfaceIndex { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string IPv4 { get; init; }
    public required string Mask { get; init; }
    public required string Broadcast { get; init; }
    public bool IsPreferredNetwork { get; init; }
    public string NetworkType { get; init; } = "Unknown";
    public int SortPriority { get; init; } = 50;

    [JsonIgnore]
    public string DisplayName => $"{Name} - {IPv4}";
}

public sealed class PeerEndpointInfo
{
    public required string Address { get; init; }
    public string NetworkType { get; set; } = "Unknown";
    public bool IsHost { get; set; }
    public int ServerPort { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class PeerAnnouncement
{
    public string App { get; set; } = "MinecraftPortable";
    public int ProtocolVersion { get; set; }
    public string PlayerName { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public string IdentityName { get; set; } = "";
    public string VpnIp { get; set; } = "";
    public string NetworkType { get; set; } = "";
    public bool IsHost { get; set; }
    public string PackHash { get; set; } = "";
    public int ServerPort { get; set; }
    public string State { get; set; } = "";
    public bool IsVoiceChannelActive { get; set; }
    public bool IsVoiceMuted { get; set; }
    public bool IsMinecraftRunning { get; set; }
    public bool IsSkinAvailable { get; set; }
    public string SkinSha256 { get; set; } = "";
    public string SkinModel { get; set; } = "classic";
    public string HostedWorldId { get; set; } = "";
    public int WaypointProtocolVersion { get; set; }
    public List<WaypointProviderAnnouncement> WaypointProviders { get; set; } = [];
}

public sealed class WaypointProviderAnnouncement
{
    public string ProviderId { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string WorldContextId { get; set; } = "";
}

public sealed class PeerViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, PeerEndpointInfo> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private string _playerName = "";
    private string _vpnIp = "";
    private string _networkType = "";
    private string _identityId = "";
    private string _identityName = "";
    private bool _isHost;
    private string _packHash = "";
    private int _serverPort;
    private bool _isInVoiceChannel;
    private bool _isSpeaking;
    private bool _isVoiceMuted;
    private bool _isMinecraftRunning;
    private bool _isSkinAvailable;
    private string _skinSha256 = "";
    private string _skinModel = "classic";
    private double _voiceVolume = 1.0d;
    private string _state = "";
    private DateTimeOffset _lastSeen;
    private string _localPackHash = "";
    private int? _lastRttMs;
    private DateTimeOffset _lastRttAt;
    private bool _isLocalVoicePeer;
    private string _hostedWorldId = "";
    private int _waypointProtocolVersion;
    private IReadOnlyList<WaypointProviderAnnouncement> _waypointProviders = Array.Empty<WaypointProviderAnnouncement>();

    public string PlayerName
    {
        get => _playerName;
        set
        {
            if (Set(ref _playerName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(VoiceDisplayName));
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }

    public string VpnIp
    {
        get => _vpnIp;
        set
        {
            if (Set(ref _vpnIp, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(VoiceDisplayName));
                OnPropertyChanged(nameof(HostDisplayName));
                OnPropertyChanged(nameof(NetworkType));
            }
        }
    }

    public string NetworkType
    {
        get => string.IsNullOrWhiteSpace(_networkType)
            ? IpBelongsToNetwork(_vpnIp)
            : _networkType;
        set
        {
            if (Set(ref _networkType, NormalizeNetworkType(value)))
            {
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }

    public string IdentityId
    {
        get => _identityId;
        set => Set(ref _identityId, value);
    }

    public string IdentityName
    {
        get => _identityName;
        set => Set(ref _identityName, value);
    }

    public bool IsHost
    {
        get => _isHost;
        set
        {
            if (Set(ref _isHost, value))
            {
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }

    public string PackHash { get => _packHash; set { if (Set(ref _packHash, value)) OnPropertyChanged(nameof(PackStatus)); } }

    public bool IsInVoiceChannel
    {
        get => _isInVoiceChannel;
        set => Set(ref _isInVoiceChannel, value);
    }

    public bool IsLocalVoicePeer
    {
        get => _isLocalVoicePeer;
        set
        {
            if (Set(ref _isLocalVoicePeer, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(VoiceDisplayName));
            }
        }
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (Set(ref _isSpeaking, value))
            {
                OnPropertyChanged(nameof(VoiceDisplayName));
            }
        }
    }

    public bool IsVoiceMuted
    {
        get => _isVoiceMuted;
        set
        {
            if (Set(ref _isVoiceMuted, value))
            {
                OnPropertyChanged(nameof(VoiceDisplayName));
            }
        }
    }

    public bool IsMinecraftRunning
    {
        get => _isMinecraftRunning;
        set => Set(ref _isMinecraftRunning, value);
    }

    public bool IsSkinAvailable
    {
        get => _isSkinAvailable;
        set => Set(ref _isSkinAvailable, value);
    }

    public string SkinSha256
    {
        get => _skinSha256;
        set => Set(ref _skinSha256, value ?? "");
    }

    public string SkinModel
    {
        get => _skinModel;
        set => Set(ref _skinModel, string.Equals(value, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic");
    }

    public double VoiceVolume
    {
        get => _voiceVolume;
        set
        {
            var clamped = Math.Clamp(value, 0d, 2d);
            if (Set(ref _voiceVolume, clamped))
            {
                OnPropertyChanged(nameof(VoiceVolumePercent));
            }
        }
    }

    public double VoiceVolumePercent => Math.Round(VoiceVolume * 100d);

    public int ServerPort
    {
        get => _serverPort;
        set
        {
            if (Set(ref _serverPort, value))
            {
                OnPropertyChanged(nameof(HostDisplayName));
            }
        }
    }
    public string State { get => _state; set => Set(ref _state, value); }
    public DateTimeOffset LastSeen { get => _lastSeen; set { if (Set(ref _lastSeen, value)) OnPropertyChanged(nameof(LastSeenText)); } }
    public string LocalPackHash { get => _localPackHash; set { if (Set(ref _localPackHash, value)) OnPropertyChanged(nameof(PackStatus)); } }
    public int? LastRttMs
    {
        get => _lastRttMs;
        set
        {
            if (Set(ref _lastRttMs, value))
            {
                OnPropertyChanged(nameof(HostDisplayName));
                OnPropertyChanged(nameof(RttDisplay));
            }
        }
    }
    public DateTimeOffset LastRttAt { get => _lastRttAt; set => Set(ref _lastRttAt, value); }
    public string HostedWorldId { get => _hostedWorldId; set => Set(ref _hostedWorldId, value); }
    public int WaypointProtocolVersion { get => _waypointProtocolVersion; set => Set(ref _waypointProtocolVersion, value); }
    public IReadOnlyList<WaypointProviderAnnouncement> WaypointProviders
    {
        get => _waypointProviders;
        set => Set(ref _waypointProviders, value ?? Array.Empty<WaypointProviderAnnouncement>());
    }

    public string DisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(PlayerName) ? "Неизвестный игрок" : PlayerName;
            return string.IsNullOrWhiteSpace(VpnIp) ? name : $"{name} - {VpnIp}";
        }
    }

    public string VoiceDisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(PlayerName) ? "Неизвестный игрок" : PlayerName;
            var localSuffix = IsLocalVoicePeer ? " (Вы)" : string.Empty;
            var address = string.IsNullOrWhiteSpace(VpnIp) ? "—" : VpnIp;
            return $"{name}{localSuffix} - {address}";
        }
    }

    [JsonIgnore]
    public IReadOnlyList<PeerEndpointInfo> NetworkEndpoints => _endpoints.Values
        .OrderByDescending(endpoint => endpoint.IsHost)
        .ThenByDescending(endpoint => endpoint.LastSeen)
        .ThenBy(endpoint => endpoint.Address, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string HostDisplayName
    {
        get
        {
            var baseName = DisplayName;
            if (IsHost && ServerPort > 0)
            {
                var hostName = $"{baseName}:{ServerPort} [{NetworkType}]";
                return LastRttMs is null ? $"{hostName} (—)" : $"{hostName} ({LastRttMs} ms)";
            }

            return baseName;
        }
    }

    public string PackStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PackHash) || PackHash == "missing") return "missing";
            if (string.IsNullOrWhiteSpace(LocalPackHash) || LocalPackHash == "missing") return "local missing";
            return string.Equals(PackHash, LocalPackHash, StringComparison.OrdinalIgnoreCase) ? "OK" : "MISMATCH";
        }
    }

    public string RttDisplay => LastRttMs is null ? "—" : $"{LastRttMs} ms";
    public string LastSeenText => LastSeen == default ? "" : LastSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(PeerAnnouncement announcement, string localPackHash)
    {
        PlayerName = announcement.PlayerName;
        IdentityId = announcement.IdentityId;
        IdentityName = announcement.IdentityName;
        IsInVoiceChannel = announcement.IsVoiceChannelActive;
        IsVoiceMuted = announcement.IsVoiceMuted;
        IsMinecraftRunning = announcement.IsMinecraftRunning;
        IsSkinAvailable = announcement.IsSkinAvailable;
        SkinSha256 = announcement.SkinSha256;
        SkinModel = announcement.SkinModel;
        HostedWorldId = announcement.HostedWorldId;
        WaypointProtocolVersion = announcement.WaypointProtocolVersion;
        WaypointProviders = announcement.WaypointProviders?.ToArray() ?? Array.Empty<WaypointProviderAnnouncement>();
        PackHash = announcement.PackHash;
        State = announcement.State;
        LocalPackHash = localPackHash;
        var now = DateTimeOffset.Now;
        LastSeen = now;

        if (!string.IsNullOrWhiteSpace(announcement.VpnIp))
        {
            if (!_endpoints.TryGetValue(announcement.VpnIp, out var endpoint))
            {
                endpoint = new PeerEndpointInfo { Address = announcement.VpnIp };
                _endpoints[announcement.VpnIp] = endpoint;
            }

            endpoint.NetworkType = NormalizeNetworkType(announcement.NetworkType);
            endpoint.IsHost = announcement.IsHost;
            endpoint.ServerPort = announcement.ServerPort;
            endpoint.LastSeen = now;
        }

        SelectPrimaryEndpoint();
    }

    public bool PruneEndpoints(DateTimeOffset cutoff)
    {
        foreach (var endpoint in _endpoints.Values.Where(endpoint => endpoint.LastSeen < cutoff).ToArray())
        {
            _endpoints.Remove(endpoint.Address);
        }

        SelectPrimaryEndpoint();
        return _endpoints.Count > 0;
    }

    public IReadOnlyList<string> GetCandidateIps(bool requireHost = false)
    {
        return _endpoints.Values
            .Where(endpoint => !requireHost || endpoint.IsHost)
            .OrderByDescending(endpoint => endpoint.Address.Equals(VpnIp, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(endpoint => endpoint.LastSeen)
            .Select(endpoint => endpoint.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SelectPrimaryEndpoint()
    {
        var primary = _endpoints.Values
            .OrderByDescending(endpoint => endpoint.IsHost)
            .ThenByDescending(endpoint => endpoint.LastSeen)
            .FirstOrDefault();
        if (primary is null)
        {
            VpnIp = "";
            NetworkType = "";
            IsHost = false;
            ServerPort = 0;
            return;
        }

        VpnIp = primary.Address;
        NetworkType = primary.NetworkType;
        IsHost = _endpoints.Values.Any(endpoint => endpoint.IsHost);
        ServerPort = _endpoints.Values
            .Where(endpoint => endpoint.IsHost && endpoint.ServerPort is > 0 and <= 65535)
            .OrderByDescending(endpoint => endpoint.LastSeen)
            .Select(endpoint => endpoint.ServerPort)
            .FirstOrDefault();
    }

    private static string NormalizeNetworkType(string? networkType)
    {
        if (string.IsNullOrWhiteSpace(networkType))
        {
            return "";
        }

        if (string.Equals(networkType, "LAN", StringComparison.OrdinalIgnoreCase))
        {
            return "LAN";
        }

        if (string.Equals(networkType, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        return "VPN";
    }

    private static string IpBelongsToNetwork(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var parsed))
        {
            return "Unknown";
        }

        var bytes = parsed.GetAddressBytes();
        if (bytes.Length != 4) return "Unknown";

        if (bytes[0] is 25 or 26 ||
            (bytes[0] == 100 && bytes[1] is >= 64 and <= 127))
        {
            return "VPN";
        }

        if (bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168))
        {
            return "LAN";
        }

        return "Unknown";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class WorldViewModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string BuildName { get; init; }
    public string DisplayName => $"{Name} ({BuildName})";
}

public sealed class ClientBuildViewModel
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public required string FullPath { get; init; }
}

public sealed class WorldTransferHeader
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string MessageType { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string SenderIdentityId { get; set; } = "";
    public string SenderIdentityName { get; set; } = "";
    public string OwnerIdentityId { get; set; } = "";
    public string OwnerIdentityName { get; set; } = "";
    public long Size { get; set; }
    public string WorldSha256 { get; set; } = "";
    public string PlayerManifestSha256 { get; set; } = "";
    public string WaypointManifestSha256 { get; set; } = "";
    public string FileName { get; set; } = "world.zip";
    public string WorldName { get; set; } = "World";
}

public sealed class WorldTransferAck
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public bool Ok { get; set; }
    public string Stage { get; set; } = "";
    public string TransferId { get; set; } = "";
    public string Message { get; set; } = "";
    public string WorldSha256 { get; set; } = "";
    public string PlayerManifestSha256 { get; set; } = "";
    public string WaypointManifestSha256 { get; set; } = "";
}

public sealed class WorldTransferControl
{
    public string Protocol { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string TransferId { get; set; } = "";
    public string Command { get; set; } = "Commit";
}

public sealed class WorldTransferJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string TransferId { get; set; } = "";
    public string Role { get; set; } = "";
    public string State { get; set; } = "";
    public string SourceWorldPath { get; set; } = "";
    public string EscrowPath { get; set; } = "";
    public string InstalledWorldPath { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorldMetadata
{
    public int SchemaVersion { get; set; } = 5;
    public string WorldId { get; set; } = "";
    public string BuildName { get; set; } = "";
    public string BuildRelativePath { get; set; } = "";
    public string PackHash { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string MarkedBy { get; set; } = "Minecraft.exe";
    public string OwnerIdentityId { get; set; } = "";
    public string OwnerIdentityName { get; set; } = "";
    public string CurrentHolderIdentityId { get; set; } = "";
    public string CurrentHolderIdentityName { get; set; } = "";
    public DateTimeOffset? LastSuccessfulTransferUtc { get; set; }
}

public sealed class WorldMetadataContext
{
    public required string BuildName { get; init; }
    public required string BuildRelativePath { get; init; }
    public required string PackHash { get; init; }
    public required string OwnerIdentityId { get; init; }
    public required string OwnerIdentityName { get; init; }
}

public sealed class VoicePacket
{
    public string PeerId { get; set; } = "";
    public int Sequence { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
}
