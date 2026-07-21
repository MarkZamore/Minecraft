using System;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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

    public int MaxMemoryGb { get; set; } = 16;

    [JsonIgnore]
    public long MaxArchiveBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    public string ClientRelativePath { get; set; } = "";
    public string NetworkName { get; set; } = "";
    public string NetworkPassword { get; set; } = "";
    public bool NetworkToolAutoLaunch { get; set; } = true;
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

public sealed class NetworkEndpointInfo
{
    public required string InterfaceId { get; init; }
    public int InterfaceIndex { get; init; }
    public required string InterfaceName { get; init; }
    public string Description { get; init; } = "";
    public required string NetworkAddress { get; init; }
    public int PrefixLength { get; init; }
    public string BroadcastAddress { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public bool IsPreferredNetwork { get; init; }
    public bool IsPhysical { get; init; }
    public bool HasDefaultRoute { get; init; }
    public string NetworkType { get; init; } = "Unknown";
    public int SortPriority { get; init; } = 50;

    [JsonIgnore]
    public AddressFamily AddressFamily => IPAddress.TryParse(NetworkAddress, out var address)
        ? address.AddressFamily
        : AddressFamily.Unspecified;

    [JsonIgnore]
    public string DisplayName => $"{InterfaceName} - {NetworkAddress}";
}

public sealed class NetworkEnvironmentSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<NetworkEndpointInfo> Endpoints { get; init; } = Array.Empty<NetworkEndpointInfo>();
    public NetworkEndpointInfo? PrimaryEndpoint { get; init; }

    public string Fingerprint => string.Join(
        "|",
        Endpoints.Select(endpoint =>
            $"{endpoint.InterfaceId}@{endpoint.NetworkAddress}/{endpoint.PrefixLength}:{endpoint.ProviderId}")) +
        $"|primary={PrimaryEndpoint?.InterfaceId}@{PrimaryEndpoint?.NetworkAddress}:{PrimaryEndpoint?.ProviderId}";
}

public sealed class PeerEndpointInfo
{
    public required string Address { get; init; }
    public string ProviderId { get; set; } = "";
    public string InterfaceId { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string NetworkType { get; set; } = "Unknown";
    public bool IsHost { get; set; }
    public int ServerPort { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class PeerAdvertisedEndpoint
{
    public string Address { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string InterfaceId { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string NetworkType { get; set; } = "Unknown";
}

public sealed class KnownPeerCache
{
    public int SchemaVersion { get; set; } = 3;
    public List<KnownPeerIdentityRecord> Peers { get; set; } = [];
}

public sealed class KnownPeerIdentityRecord
{
    public string IdentityId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public List<KnownPeerEndpointRecord> Endpoints { get; set; } = [];
}

public sealed class KnownPeerEndpointRecord
{
    public string Address { get; set; } = "";
    public string Ip { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string InterfaceId { get; set; } = "";
    public string NetworkType { get; set; } = "Unknown";
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSuccessUtc { get; set; }
    public bool IsObserved { get; set; }
    public bool IsConfirmed { get; set; }
    public int FailureScore { get; set; }
    public long Revision { get; set; }
}

public sealed class PeerAnnouncement
{
    public string App { get; set; } = "MinecraftPortable";
    public int ProtocolVersion { get; set; }
    public string PlayerName { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public string IdentityName { get; set; } = "";
    [JsonPropertyName("vpnIp")]
    public string NetworkAddress { get; set; } = "";
    public string NetworkProviderId { get; set; } = "";
    public string NetworkInterfaceId { get; set; } = "";
    public string NetworkAddressFamily { get; set; } = "";
    public string NetworkType { get; set; } = "";
    public bool IsDirectedReply { get; set; }
    public List<PeerAdvertisedEndpoint> NetworkEndpoints { get; set; } = [];
    public bool IsHost { get; set; }
    public string PackHash { get; set; } = "";
    public int ServerPort { get; set; }
    public string LanSessionId { get; set; } = "";
    public string State { get; set; } = "";
    public bool IsVoiceChannelActive { get; set; }
    public bool IsVoiceMuted { get; set; }
    public bool IsMinecraftRunning { get; set; }
    public bool IsMinecraftPreparing { get; set; }
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
    private string _networkAddress = "";
    private string _networkType = "";
    private string _preferredProviderId = "";
    private string _primaryEndpointKey = "";
    private string _identityId = "";
    private string _identityName = "";
    private bool _isHost;
    private string _packHash = "";
    private int _serverPort;
    private string _lanSessionId = "";
    private bool _isInVoiceChannel;
    private bool _isSpeaking;
    private bool _isVoiceMuted;
    private bool _isMinecraftRunning;
    private bool _isMinecraftPreparing;
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

    public string NetworkAddress
    {
        get => _networkAddress;
        set
        {
            if (Set(ref _networkAddress, value))
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
            ? AddressBelongsToNetwork(_networkAddress)
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

    public bool IsMinecraftPreparing
    {
        get => _isMinecraftPreparing;
        set => Set(ref _isMinecraftPreparing, value);
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
    public string LanSessionId { get => _lanSessionId; set => Set(ref _lanSessionId, value ?? ""); }
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
            var address = AddressDisplay;
            return string.IsNullOrWhiteSpace(address) ? name : $"{name} - {address}";
        }
    }

    public string VoiceDisplayName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(PlayerName) ? "Неизвестный игрок" : PlayerName;
            var localSuffix = IsLocalVoicePeer ? " (Вы)" : string.Empty;
            var address = string.IsNullOrWhiteSpace(AddressDisplay) ? "—" : AddressDisplay;
            return $"{name}{localSuffix} - {address}";
        }
    }

    public string AddressDisplay
    {
        get
        {
            _endpoints.TryGetValue(_primaryEndpointKey, out var primary);
            if (primary is null) return NetworkAddress;

            var grouped = _endpoints.Values
                .Where(endpoint => IsSameDisplayGroup(endpoint, primary))
                .OrderByDescending(endpoint => endpoint.LastSeen)
                .ToArray();
            var ipv4 = grouped.FirstOrDefault(endpoint => IsAddressFamily(endpoint, AddressFamily.InterNetwork))?.Address;
            var ipv6 = grouped.FirstOrDefault(endpoint => IsAddressFamily(endpoint, AddressFamily.InterNetworkV6))?.Address;
            if (!string.IsNullOrWhiteSpace(ipv4) && !string.IsNullOrWhiteSpace(ipv6)) return $"{ipv4} ({ipv6})";
            return ipv4 ?? ipv6 ?? NetworkAddress;
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
        IsMinecraftPreparing = announcement.IsMinecraftPreparing;
        IsSkinAvailable = announcement.IsSkinAvailable;
        SkinSha256 = announcement.SkinSha256;
        SkinModel = announcement.SkinModel;
        HostedWorldId = announcement.HostedWorldId;
        WaypointProtocolVersion = announcement.WaypointProtocolVersion;
        WaypointProviders = announcement.WaypointProviders?.ToArray() ?? Array.Empty<WaypointProviderAnnouncement>();
        PackHash = announcement.PackHash;
        LanSessionId = announcement.LanSessionId;
        State = announcement.State;
        LocalPackHash = localPackHash;
        var now = DateTimeOffset.Now;
        LastSeen = now;

        var announcedEndpoints = (announcement.NetworkEndpoints ?? [])
            .Concat(string.IsNullOrWhiteSpace(announcement.NetworkAddress)
                ? []
                : [new PeerAdvertisedEndpoint
                {
                    Address = announcement.NetworkAddress,
                    ProviderId = announcement.NetworkProviderId,
                    InterfaceId = announcement.NetworkInterfaceId,
                    AddressFamily = announcement.NetworkAddressFamily,
                    NetworkType = announcement.NetworkType
                }])
            .Where(item => !string.IsNullOrWhiteSpace(item.Address))
            .GroupBy(item => GetEndpointKey(item.Address, item.ProviderId, item.InterfaceId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        foreach (var announcedEndpoint in announcedEndpoints)
        {
            var endpointKey = GetEndpointKey(
                announcedEndpoint.Address,
                announcedEndpoint.ProviderId,
                announcedEndpoint.InterfaceId);
            if (!_endpoints.TryGetValue(endpointKey, out var endpoint))
            {
                endpoint = new PeerEndpointInfo { Address = announcedEndpoint.Address };
                _endpoints[endpointKey] = endpoint;
            }

            endpoint.ProviderId = announcedEndpoint.ProviderId?.Trim() ?? "";
            endpoint.InterfaceId = announcedEndpoint.InterfaceId?.Trim() ?? "";
            endpoint.AddressFamily = announcedEndpoint.AddressFamily?.Trim() ?? "";
            endpoint.NetworkType = NormalizeNetworkType(announcedEndpoint.NetworkType);
            endpoint.IsHost = announcement.IsHost;
            endpoint.ServerPort = announcement.ServerPort;
            endpoint.LastSeen = now;
        }

        SelectPrimaryEndpoint();
        NotifyAddressDisplayChanged();
    }

    public bool PruneEndpoints(DateTimeOffset cutoff)
    {
        foreach (var pair in _endpoints.Where(pair => pair.Value.LastSeen < cutoff).ToArray())
        {
            _endpoints.Remove(pair.Key);
        }

        SelectPrimaryEndpoint();
        NotifyAddressDisplayChanged();
        return _endpoints.Count > 0;
    }

    public void SetLocalEndpoints(
        IEnumerable<NetworkEndpointInfo> endpoints,
        NetworkEndpointInfo? preferredEndpoint)
    {
        _endpoints.Clear();
        var now = DateTimeOffset.Now;
        foreach (var endpoint in endpoints)
        {
            var item = new PeerEndpointInfo
            {
                Address = endpoint.NetworkAddress,
                ProviderId = endpoint.ProviderId,
                InterfaceId = endpoint.InterfaceId,
                AddressFamily = endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                NetworkType = endpoint.NetworkType,
                LastSeen = now
            };
            _endpoints[GetEndpointKey(item.Address, item.ProviderId, item.InterfaceId)] = item;
        }

        _preferredProviderId = preferredEndpoint?.ProviderId?.Trim() ?? "";
        SelectPrimaryEndpoint();
        if (preferredEndpoint is not null &&
            _endpoints.Values.FirstOrDefault(endpoint =>
                string.Equals(endpoint.Address, preferredEndpoint.NetworkAddress, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(endpoint.ProviderId, preferredEndpoint.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(endpoint.InterfaceId, preferredEndpoint.InterfaceId, StringComparison.OrdinalIgnoreCase)) is { } preferred)
        {
            _primaryEndpointKey = GetEndpointKey(preferred.Address, preferred.ProviderId, preferred.InterfaceId);
            NetworkAddress = preferred.Address;
        }
        NotifyAddressDisplayChanged();
    }

    public IReadOnlyList<string> GetCandidateAddresses(bool requireHost = false, string? preferredProviderId = null)
        => GetCandidateEndpoints(requireHost, preferredProviderId)
            .Select(endpoint => endpoint.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<PeerEndpointInfo> GetCandidateEndpoints(
        bool requireHost = false,
        string? preferredProviderId = null)
    {
        var preferred = string.IsNullOrWhiteSpace(preferredProviderId)
            ? _preferredProviderId
            : preferredProviderId;
        return _endpoints.Values
            .Where(endpoint => !requireHost || endpoint.IsHost)
            .OrderByDescending(endpoint => !string.IsNullOrWhiteSpace(preferred) &&
                                           string.Equals(endpoint.ProviderId, preferred, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(endpoint => endpoint.Address.Equals(NetworkAddress, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(endpoint => endpoint.LastSeen)
            .GroupBy(
                endpoint => GetEndpointKey(endpoint.Address, endpoint.ProviderId, endpoint.InterfaceId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public void SetPreferredProvider(string? providerId)
    {
        _preferredProviderId = providerId?.Trim() ?? "";
        SelectPrimaryEndpoint();
        NotifyAddressDisplayChanged();
    }

    private void SelectPrimaryEndpoint()
    {
        var primary = _endpoints.Values
            .OrderByDescending(endpoint => !string.IsNullOrWhiteSpace(_preferredProviderId) &&
                                           string.Equals(endpoint.ProviderId, _preferredProviderId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(endpoint => endpoint.IsHost)
            .ThenByDescending(endpoint => endpoint.LastSeen)
            .FirstOrDefault();
        if (primary is null)
        {
            _primaryEndpointKey = "";
            NetworkAddress = "";
            NetworkType = "";
            IsHost = false;
            ServerPort = 0;
            return;
        }

        _primaryEndpointKey = GetEndpointKey(primary.Address, primary.ProviderId, primary.InterfaceId);
        NetworkAddress = primary.Address;
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

    private static bool IsSameDisplayGroup(PeerEndpointInfo left, PeerEndpointInfo right)
    {
        if (!string.IsNullOrWhiteSpace(left.InterfaceId) || !string.IsNullOrWhiteSpace(right.InterfaceId))
        {
            return string.Equals(left.InterfaceId, right.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.ProviderId, right.ProviderId, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.ProviderId, right.ProviderId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAddressFamily(PeerEndpointInfo endpoint, AddressFamily family)
    {
        if (IPAddress.TryParse(endpoint.Address, out var parsed)) return parsed.AddressFamily == family;
        return family == AddressFamily.InterNetworkV6
            ? string.Equals(endpoint.AddressFamily, "IPv6", StringComparison.OrdinalIgnoreCase)
            : string.Equals(endpoint.AddressFamily, "IPv4", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEndpointKey(string address, string? providerId, string? interfaceId) =>
        $"{providerId?.Trim()}|{interfaceId?.Trim()}|{address.Trim()}";

    private void NotifyAddressDisplayChanged()
    {
        OnPropertyChanged(nameof(AddressDisplay));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(VoiceDisplayName));
        OnPropertyChanged(nameof(HostDisplayName));
    }

    private static string AddressBelongsToNetwork(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var parsed))
        {
            return "Unknown";
        }

        var bytes = parsed.GetAddressBytes();
        if (bytes.Length == 16)
        {
            return (bytes[0] & 0xFE) == 0xFC ? "LAN" : "Unknown";
        }
        if (bytes.Length != 4) return "Unknown";

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
