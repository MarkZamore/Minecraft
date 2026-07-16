using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class PeerDiscoveryService : IAsyncDisposable
{
    public const int ProtocolVersion = 4;
    private const int DiscoveryPort = 35655;
    private const int MaxKnownPeers = 64;
    private const int MaxFullSubnetProbeSize = 512;
    private static readonly IPAddress IPv6MulticastGroup = IPAddress.Parse("ff12::4d43:5054");
    private static readonly TimeSpan BaseSendInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSendInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan KnownPeerTtl = TimeSpan.FromDays(30);

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly VirtualNetworkService _network;
    private readonly NetworkNeighborService _neighborService = new();
    private readonly List<SenderEntry> _senders = [];
    private readonly List<UdpClient> _listeners = [];
    private readonly HashSet<string> _localAdvertisedAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KnownPeerRecord> _knownPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, IReadOnlyList<IPAddress>> _neighborTargets = [];
    private readonly Dictionary<string, DateTimeOffset> _lastDirectedReplies = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _peerGate = new();
    private readonly object _senderGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Random _random = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task[] _receiveLoopTasks = [];
    private Task? _sendLoopTask;
    private bool _knownPeersDirty;
    private DateTimeOffset _lastKnownPeerSave = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNeighborWarning = DateTimeOffset.MinValue;
    private Func<NetworkEndpointInfo, PeerAnnouncement>? _createAnnouncement;
    private NetworkEnvironmentSnapshot _snapshot = new();
    private IReadOnlyList<IPAddress> _dynamicTargets = Array.Empty<IPAddress>();

    public PeerDiscoveryService(AppPaths paths, Logger logger, VirtualNetworkService network)
    {
        _paths = paths;
        _logger = logger;
        _network = network;
    }

    public event Action<PeerAnnouncement>? PeerUpdated;

    public async Task StartAsync(
        NetworkEnvironmentSnapshot snapshot,
        Func<NetworkEndpointInfo, PeerAnnouncement> createAnnouncement)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            LoadKnownPeers();
            PruneKnownPeers();
            _snapshot = snapshot;
            _createAnnouncement = createAnnouncement;
            _cts = new CancellationTokenSource();

            ConfigureListeners(snapshot.Endpoints);
            foreach (var endpoint in snapshot.Endpoints)
            {
                if (!IPAddress.TryParse(endpoint.NetworkAddress, out var localAddress)) continue;
                try
                {
                    var sender = _network.CreateBoundUdpClient(endpoint, 0, reuseAddress: false);
                    if (localAddress.AddressFamily == AddressFamily.InterNetwork)
                    {
                        sender.EnableBroadcast = true;
                    }
                    else
                    {
                        sender.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastInterface, endpoint.InterfaceIndex);
                    }
                    var targets = BuildTargets(endpoint);
                    lock (_senderGate)
                    {
                        _senders.Add(new SenderEntry(sender, endpoint, targets));
                        _localAdvertisedAddresses.Add(endpoint.NetworkAddress);
                    }
                    _logger.Info(
                        $"Peer discovery sender configured for {endpoint.InterfaceName} " +
                        $"({endpoint.NetworkAddress}/{endpoint.PrefixLength}) with {targets.Count} target(s).");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Peer discovery sender init failed for '{endpoint.InterfaceName}': {ex.Message}");
                }
            }

            await RefreshDynamicTargetsAsync(_cts.Token).ConfigureAwait(false);
            _receiveLoopTasks = _listeners
                .Select(listener => ReceiveLoopAsync(listener, null, _cts.Token))
                .Concat(_senders.Select(sender =>
                    ReceiveLoopAsync(sender.Sender, sender.Endpoint, _cts.Token)))
                .ToArray();
            _sendLoopTask = SendLoopAsync(createAnnouncement, _cts.Token);
            _logger.Info($"Peer discovery started on {_senders.Count} endpoint(s): " +
                string.Join(", ", _senders.Select(sender =>
                    $"{sender.Endpoint.InterfaceName} [{sender.Endpoint.NetworkAddress}]").Take(12)));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void ConfigureListeners(IReadOnlyList<NetworkEndpointInfo> endpoints)
    {
        if (endpoints.Any(endpoint => endpoint.AddressFamily == AddressFamily.InterNetwork))
        {
            try
            {
                var listener = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _listeners.Add(listener);
            }
            catch (SocketException ex)
            {
                _logger.Warn($"IPv4 discovery listener is unavailable: {ex.Message}");
            }
        }

        var ipv6Interfaces = endpoints
            .Where(endpoint => endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            .Select(endpoint => endpoint.InterfaceIndex)
            .Distinct()
            .ToArray();
        if (ipv6Interfaces.Length == 0) return;

        try
        {
            var ipv6Listener = new UdpClient(AddressFamily.InterNetworkV6);
            ipv6Listener.Client.DualMode = false;
            ipv6Listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            ipv6Listener.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, DiscoveryPort));
            foreach (var interfaceIndex in ipv6Interfaces)
            {
                try
                {
                    ipv6Listener.JoinMulticastGroup(interfaceIndex, IPv6MulticastGroup);
                }
                catch (SocketException ex)
                {
                    _logger.Warn($"IPv6 discovery multicast is unavailable on interface {interfaceIndex}: {ex.Message}");
                }
            }
            _listeners.Add(ipv6Listener);
        }
        catch (SocketException ex)
        {
            _logger.Warn($"IPv6 discovery listener is unavailable: {ex.Message}");
        }
    }

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        SenderEntry[] senders;
        UdpClient[] listeners;
        lock (_senderGate)
        {
            senders = _senders.ToArray();
            listeners = _listeners.ToArray();
            _senders.Clear();
            _listeners.Clear();
            _localAdvertisedAddresses.Clear();
        }
        var tasks = _receiveLoopTasks.Concat(_sendLoopTask is null ? [] : [_sendLoopTask]).ToArray();
        _cts = null;
        _receiveLoopTasks = [];
        _sendLoopTask = null;
        _createAnnouncement = null;
        _snapshot = new NetworkEnvironmentSnapshot();
        _dynamicTargets = Array.Empty<IPAddress>();
        lock (_peerGate)
        {
            _neighborTargets.Clear();
            _lastDirectedReplies.Clear();
        }

        cts?.Cancel();
        foreach (var listener in listeners) listener.Dispose();
        foreach (var sender in senders) sender.Sender.Dispose();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
        cts?.Dispose();
        PersistKnownPeers(force: true);
    }

    private List<DiscoveryTarget> BuildTargets(NetworkEndpointInfo endpoint)
    {
        var result = new List<DiscoveryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            if (VirtualNetworkService.SupportsDirectedBroadcast(endpoint) &&
                IPAddress.TryParse(endpoint.BroadcastAddress, out var broadcast))
            {
                Add(new IPEndPoint(broadcast, DiscoveryPort), false);
            }
            foreach (var address in VirtualNetworkService.EnumerateProbeAddresses(endpoint, MaxFullSubnetProbeSize))
            {
                if (!IsLocalAddress(address.ToString())) Add(new IPEndPoint(address, DiscoveryPort), true);
            }
        }
        else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var scopedGroup = new IPAddress(IPv6MulticastGroup.GetAddressBytes(), endpoint.InterfaceIndex);
            Add(new IPEndPoint(scopedGroup, DiscoveryPort), false);
        }
        return result;

        void Add(IPEndPoint target, bool probe)
        {
            if (seen.Add(target.ToString())) result.Add(new DiscoveryTarget(target, probe));
        }
    }

    private async Task ReceiveLoopAsync(
        UdpClient listener,
        NetworkEndpointInfo? receivingEndpoint,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await listener.ReceiveAsync(token).ConfigureAwait(false);
                var announcement = JsonSerializer.Deserialize<PeerAnnouncement>(
                    Encoding.UTF8.GetString(result.Buffer),
                    _jsonOptions);
                if (announcement?.App != "MinecraftPortable" || announcement.ProtocolVersion != ProtocolVersion) continue;

                var peerAddress = ResolvePeerAddress(result.RemoteEndPoint);
                if (peerAddress is null) continue;
                announcement.NetworkAddress = peerAddress.ToString();
                announcement.NetworkAddressFamily = peerAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
                SanitizeAdvertisedEndpoints(announcement, peerAddress);
                RememberPeer(announcement);
                PeerUpdated?.Invoke(announcement);
                if (!announcement.IsDirectedReply)
                {
                    await SendDirectedReplyAsync(
                        result.RemoteEndPoint,
                        announcement.NetworkProviderId,
                        receivingEndpoint,
                        token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException && token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Peer discovery receive failed: {ex.Message}");
            }
        }
    }

    private async Task SendLoopAsync(
        Func<NetworkEndpointInfo, PeerAnnouncement> createAnnouncement,
        CancellationToken token)
    {
        SenderEntry[] senderSnapshot;
        lock (_senderGate) senderSnapshot = _senders.ToArray();
        if (senderSnapshot.Length == 0)
        {
            _logger.Warn("Peer discovery has no usable network endpoints.");
            return;
        }

        var tick = 0;
        while (!token.IsCancellationRequested)
        {
            tick++;
            var includeProbes = tick % 5 == 1;
            if (includeProbes) await RefreshDynamicTargetsAsync(token).ConfigureAwait(false);
            foreach (var entry in senderSnapshot)
            {
                if (DateTimeOffset.UtcNow < entry.NextAttemptUtc) continue;
                try
                {
                    var announcement = createAnnouncement(entry.Endpoint);
                    announcement.NetworkAddress = entry.Endpoint.NetworkAddress;
                    announcement.NetworkProviderId = entry.Endpoint.ProviderId;
                    announcement.NetworkAddressFamily = entry.Endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
                    announcement.IsDirectedReply = false;
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
                    foreach (var target in BuildCurrentTargets(entry, includeProbes))
                    {
                        await entry.Sender.SendAsync(bytes, target.Endpoint, token).ConfigureAwait(false);
                    }
                    entry.FailureDelay = BaseSendInterval;
                    entry.NextAttemptUtc = DateTimeOffset.MinValue;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException && token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    entry.FailureDelay = TimeSpan.FromMilliseconds(Math.Min(
                        MaxSendInterval.TotalMilliseconds,
                        Math.Max(BaseSendInterval.TotalMilliseconds, entry.FailureDelay.TotalMilliseconds + 700)));
                    entry.NextAttemptUtc = DateTimeOffset.UtcNow + entry.FailureDelay +
                                           TimeSpan.FromMilliseconds(_random.Next(150, 450));
                    if (DateTimeOffset.UtcNow - entry.LastWarningUtc >= TimeSpan.FromSeconds(15))
                    {
                        entry.LastWarningUtc = DateTimeOffset.UtcNow;
                        _logger.Warn($"Peer discovery send failed for '{entry.Endpoint.InterfaceName}': {ex.Message}");
                    }
                }
            }

            var delay = BaseSendInterval + TimeSpan.FromMilliseconds(_random.Next(-120, 180));
            if (delay < TimeSpan.FromMilliseconds(600)) delay = TimeSpan.FromMilliseconds(600);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
    }

    private List<DiscoveryTarget> BuildCurrentTargets(SenderEntry entry, bool includeProbes)
    {
        var targets = new List<DiscoveryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in entry.Targets)
        {
            if (!target.IsProbe || includeProbes) Add(target.Endpoint, target.IsProbe);
        }

        KnownPeerRecord[] known;
        IReadOnlyList<IPAddress> neighbors;
        lock (_peerGate)
        {
            known = _knownPeers.Values.ToArray();
            neighbors = _neighborTargets.TryGetValue(entry.Endpoint.InterfaceIndex, out var values)
                ? values
                : Array.Empty<IPAddress>();
        }

        foreach (var peer in known)
        {
            var address = ParseAddress(peer.Address);
            if (address is null ||
                address.AddressFamily != entry.Endpoint.AddressFamily ||
                IsLocalAddress(address.ToString()))
            {
                continue;
            }
            if (EndpointCanReach(address, entry.Endpoint, peer.ProviderId))
            {
                Add(new IPEndPoint(address, DiscoveryPort), false);
            }
        }

        foreach (var address in neighbors
                     .Concat(_dynamicTargets)
                     .Distinct())
        {
            if (address.AddressFamily != entry.Endpoint.AddressFamily || IsLocalAddress(address.ToString())) continue;
            if (EndpointCanReach(address, entry.Endpoint, null))
            {
                Add(new IPEndPoint(address, DiscoveryPort), false);
            }
        }
        return targets;

        void Add(IPEndPoint target, bool probe)
        {
            if (target.AddressFamily == entry.Endpoint.AddressFamily && seen.Add(target.ToString()))
            {
                targets.Add(new DiscoveryTarget(target, probe));
            }
        }
    }

    private async Task RefreshDynamicTargetsAsync(CancellationToken token)
    {
        try
        {
            var neighbors = _neighborService.GetNeighbors();
            lock (_peerGate)
            {
                _neighborTargets.Clear();
                foreach (var pair in neighbors) _neighborTargets[pair.Key] = pair.Value;
            }
            _dynamicTargets = await _network.GetDynamicPeerTargetsAsync(_snapshot, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (DateTimeOffset.UtcNow - _lastNeighborWarning < TimeSpan.FromMinutes(1)) return;
            _lastNeighborWarning = DateTimeOffset.UtcNow;
            _logger.Warn($"Could not refresh network discovery targets: {ex.Message}");
        }
    }

    private async Task SendDirectedReplyAsync(
        IPEndPoint peerEndpoint,
        string? providerId,
        NetworkEndpointInfo? receivingEndpoint,
        CancellationToken token)
    {
        var peerAddress = peerEndpoint.Address;
        if (IsLocalAddress(peerAddress.ToString())) return;
        var factory = _createAnnouncement;
        if (factory is null) return;
        var key = peerEndpoint.ToString();
        var now = DateTimeOffset.UtcNow;
        lock (_peerGate)
        {
            if (_lastDirectedReplies.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromSeconds(3)) return;
            _lastDirectedReplies[key] = now;
        }

        SenderEntry[] senders;
        lock (_senderGate) senders = _senders.ToArray();
        var selectedEndpoint = receivingEndpoint ??
            _network.SelectLocalEndpoint(peerAddress, providerId);
        foreach (var entry in senders
                     .Where(sender => sender.Endpoint.AddressFamily == peerAddress.AddressFamily)
                     .OrderByDescending(sender => IsSameEndpoint(sender.Endpoint, selectedEndpoint))
                     .ThenByDescending(sender => RouteUsesEndpoint(peerAddress, sender.Endpoint)))
        {
            if (!IsSameEndpoint(entry.Endpoint, selectedEndpoint) &&
                !RouteUsesEndpoint(peerAddress, entry.Endpoint))
            {
                continue;
            }
            try
            {
                var announcement = factory(entry.Endpoint);
                announcement.NetworkAddress = entry.Endpoint.NetworkAddress;
                announcement.NetworkProviderId = entry.Endpoint.ProviderId;
                announcement.NetworkAddressFamily = entry.Endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
                announcement.IsDirectedReply = true;
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
                await entry.Sender.SendAsync(bytes, peerEndpoint, token).ConfigureAwait(false);
                if (peerEndpoint.Port != DiscoveryPort)
                {
                    await entry.Sender.SendAsync(
                        bytes,
                        new IPEndPoint(peerAddress, DiscoveryPort),
                        token).ConfigureAwait(false);
                }
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                _logger.Warn($"Peer discovery directed reply to {peerAddress} failed: {ex.Message}");
            }
        }
    }

    private static bool IsSameEndpoint(
        NetworkEndpointInfo endpoint,
        NetworkEndpointInfo? selectedEndpoint) =>
        selectedEndpoint is not null &&
        string.Equals(endpoint.InterfaceId, selectedEndpoint.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(endpoint.NetworkAddress, selectedEndpoint.NetworkAddress, StringComparison.OrdinalIgnoreCase);

    private static bool RouteUsesEndpoint(IPAddress target, NetworkEndpointInfo endpoint)
    {
        if (target.AddressFamily != endpoint.AddressFamily ||
            !IPAddress.TryParse(endpoint.NetworkAddress, out var localAddress))
        {
            return false;
        }
        if (VirtualNetworkService.IsInSameNetwork(target, endpoint)) return true;
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, DiscoveryPort));
            return socket.LocalEndPoint is IPEndPoint local && local.Address.Equals(localAddress);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private bool EndpointCanReach(
        IPAddress target,
        NetworkEndpointInfo endpoint,
        string? providerId)
    {
        var selected = _network.SelectLocalEndpoint(target, providerId);
        if (IsSameEndpoint(endpoint, selected)) return true;
        return RouteUsesEndpoint(target, endpoint);
    }

    private static IPAddress? ResolvePeerAddress(IPEndPoint remote) =>
        remote.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            ? remote.Address
            : null;

    private void SanitizeAdvertisedEndpoints(PeerAnnouncement announcement, IPAddress observedAddress)
    {
        var providerId = announcement.NetworkProviderId?.Trim() ?? "";
        var interfaceId = announcement.NetworkInterfaceId?.Trim() ?? "";
        var endpoints = (announcement.NetworkEndpoints ?? [])
            .Where(endpoint =>
                string.Equals(endpoint.ProviderId?.Trim(), providerId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(endpoint.InterfaceId?.Trim(), interfaceId, StringComparison.OrdinalIgnoreCase) &&
                TryGetUsablePeerAddress(endpoint.Address, out _))
            .Take(8)
            .Select(endpoint => new PeerAdvertisedEndpoint
            {
                Address = endpoint.Address.Trim(),
                ProviderId = providerId,
                InterfaceId = interfaceId,
                AddressFamily = IPAddress.Parse(endpoint.Address).AddressFamily == AddressFamily.InterNetworkV6
                    ? "IPv6"
                    : "IPv4",
                NetworkType = VirtualNetworkService.NormalizeNetworkType(endpoint.NetworkType)
            })
            .ToList();

        if (!IsLocalAddress(observedAddress.ToString()) &&
            endpoints.All(endpoint => !string.Equals(
                endpoint.Address,
                observedAddress.ToString(),
                StringComparison.OrdinalIgnoreCase)))
        {
            endpoints.Insert(0, new PeerAdvertisedEndpoint
            {
                Address = observedAddress.ToString(),
                ProviderId = providerId,
                InterfaceId = interfaceId,
                AddressFamily = observedAddress.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                NetworkType = VirtualNetworkService.NormalizeNetworkType(announcement.NetworkType)
            });
        }

        announcement.NetworkEndpoints = endpoints;
    }

    private static bool TryGetUsablePeerAddress(string? value, out IPAddress address)
    {
        if (!IPAddress.TryParse(value, out address!)) return false;
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6Multicast ||
            address.IsIPv6LinkLocal)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 0 && !(bytes[0] == 169 && bytes[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private void RememberPeer(PeerAnnouncement announcement)
    {
        var now = DateTimeOffset.UtcNow;
        var addresses = (announcement.NetworkEndpoints ?? [])
            .Select(endpoint => endpoint.Address)
            .Append(announcement.NetworkAddress)
            .Where(address => TryGetUsablePeerAddress(address, out _) && !IsLocalAddress(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (addresses.Length == 0) return;
        var persist = false;
        lock (_peerGate)
        {
            var advertisedEndpoints = announcement.NetworkEndpoints ?? [];
            foreach (var address in addresses)
            {
                var advertised = advertisedEndpoints.FirstOrDefault(endpoint =>
                    string.Equals(endpoint.Address, address, StringComparison.OrdinalIgnoreCase));
                var isNew = !_knownPeers.TryGetValue(address, out var peer);
                peer ??= new KnownPeerRecord { Address = address };
                peer.IdentityId = announcement.IdentityId;
                peer.PlayerName = announcement.PlayerName;
                peer.ProviderId = advertised?.ProviderId ?? announcement.NetworkProviderId;
                peer.NetworkType = VirtualNetworkService.NormalizeNetworkType(
                    advertised?.NetworkType ?? announcement.NetworkType);
                peer.LastSeenUtc = now;
                _knownPeers[address] = peer;
                persist |= isNew;
            }
            _knownPeersDirty = true;
            persist |= now - _lastKnownPeerSave > TimeSpan.FromMinutes(5);
        }
        if (persist) PersistKnownPeers(force: true);
    }

    private void LoadKnownPeers()
    {
        try
        {
            if (!File.Exists(_paths.NetworkPeersFile)) return;
            var cache = JsonSerializer.Deserialize<KnownPeerCache>(File.ReadAllText(_paths.NetworkPeersFile), _jsonOptions);
            lock (_peerGate)
            {
                _knownPeers.Clear();
                foreach (var identity in cache?.Peers ?? [])
                {
                    foreach (var endpoint in identity.Endpoints)
                    {
                        var address = string.IsNullOrWhiteSpace(endpoint.Address) ? endpoint.Ip : endpoint.Address;
                        if (ParseAddress(address) is null || DateTimeOffset.UtcNow - endpoint.LastSeenUtc > KnownPeerTtl) continue;
                        _knownPeers[address] = new KnownPeerRecord
                        {
                            Address = address,
                            IdentityId = identity.IdentityId,
                            PlayerName = identity.PlayerName,
                            ProviderId = endpoint.ProviderId,
                            NetworkType = endpoint.NetworkType,
                            LastSeenUtc = endpoint.LastSeenUtc
                        };
                    }
                }
                _knownPeersDirty = false;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not read known network peers: {ex.Message}");
        }
    }

    private void PruneKnownPeers()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_peerGate)
        {
            foreach (var peer in _knownPeers.Values.Where(peer => now - peer.LastSeenUtc > KnownPeerTtl).ToArray())
            {
                _knownPeers.Remove(peer.Address);
                _knownPeersDirty = true;
            }
            foreach (var peer in _knownPeers.Values.OrderByDescending(peer => peer.LastSeenUtc).Skip(MaxKnownPeers).ToArray())
            {
                _knownPeers.Remove(peer.Address);
                _knownPeersDirty = true;
            }
        }
    }

    private void PersistKnownPeers(bool force)
    {
        try
        {
            lock (_peerGate)
            {
                if (!force && !_knownPeersDirty) return;
                if (!_knownPeersDirty && File.Exists(_paths.NetworkPeersFile)) return;
                var cache = new KnownPeerCache
                {
                    Peers = _knownPeers.Values
                        .OrderByDescending(peer => peer.LastSeenUtc)
                        .Take(MaxKnownPeers)
                        .GroupBy(peer => string.IsNullOrWhiteSpace(peer.IdentityId)
                            ? $"endpoint:{peer.Address}"
                            : peer.IdentityId, StringComparer.OrdinalIgnoreCase)
                        .Select(group => new KnownPeerIdentityRecord
                        {
                            IdentityId = group.Key.StartsWith("endpoint:", StringComparison.Ordinal) ? "" : group.Key,
                            PlayerName = group.OrderByDescending(peer => peer.LastSeenUtc).First().PlayerName,
                            Endpoints = group.Select(peer => new KnownPeerEndpointRecord
                            {
                                Address = peer.Address,
                                ProviderId = peer.ProviderId,
                                NetworkType = peer.NetworkType,
                                LastSeenUtc = peer.LastSeenUtc
                            }).ToList()
                        }).ToList()
                };
                AtomicFile.WriteAllText(_paths.NetworkPeersFile, JsonSerializer.Serialize(cache, _jsonOptions));
                _knownPeersDirty = false;
                _lastKnownPeerSave = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not write known network peers: {ex.Message}");
        }
    }

    public bool IsLocalAddress(string address)
    {
        lock (_senderGate) return _localAdvertisedAddresses.Contains(address);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private static IPAddress? ParseAddress(string? value) =>
        IPAddress.TryParse(value, out var address) ? address : null;

    private sealed class SenderEntry
    {
        public SenderEntry(UdpClient sender, NetworkEndpointInfo endpoint, IReadOnlyList<DiscoveryTarget> targets)
        {
            Sender = sender;
            Endpoint = endpoint;
            Targets = targets;
        }
        public UdpClient Sender { get; }
        public NetworkEndpointInfo Endpoint { get; }
        public IReadOnlyList<DiscoveryTarget> Targets { get; }
        public TimeSpan FailureDelay { get; set; } = BaseSendInterval;
        public DateTimeOffset NextAttemptUtc { get; set; }
        public DateTimeOffset LastWarningUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed record DiscoveryTarget(IPEndPoint Endpoint, bool IsProbe);

    private sealed class KnownPeerRecord
    {
        public string Address { get; set; } = "";
        public string IdentityId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string NetworkType { get; set; } = "Unknown";
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class KnownPeerCache
    {
        public int SchemaVersion { get; set; } = 3;
        public List<KnownPeerIdentityRecord> Peers { get; set; } = [];
    }

    private sealed class KnownPeerIdentityRecord
    {
        public string IdentityId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public List<KnownPeerEndpointRecord> Endpoints { get; set; } = [];
    }

    private sealed class KnownPeerEndpointRecord
    {
        public string Address { get; set; } = "";
        public string Ip { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string NetworkType { get; set; } = "Unknown";
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
