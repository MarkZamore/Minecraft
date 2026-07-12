using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class PeerDiscoveryService : IAsyncDisposable
{
    public const int ProtocolVersion = 2;
    private const int DiscoveryPort = 35655;
    private const int MaxKnownPeers = 64;
    private const int MaxFullSubnetProbeSize = 512;
    private static readonly TimeSpan BaseSendInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSendInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan KnownPeerTtl = TimeSpan.FromDays(30);

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly NetworkNeighborService _neighborService = new();
    private readonly List<SenderEntry> _senders = new();
    private readonly List<string> _localAdvertisedIps = new();
    private readonly Dictionary<string, KnownPeerRecord> _knownPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, IReadOnlyList<IPAddress>> _neighborTargets = new();
    private readonly Dictionary<string, DateTimeOffset> _lastDirectedReplies = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _peerGate = new();
    private readonly object _senderGate = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Random _random = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private UdpClient? _listener;
    private Task? _receiveLoopTask;
    private Task? _sendLoopTask;
    private bool _knownPeersDirty;
    private DateTimeOffset _lastKnownPeerSave = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNeighborWarning = DateTimeOffset.MinValue;
    private Func<NetworkAdapterInfo, PeerAnnouncement>? _createAnnouncement;

    public PeerDiscoveryService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public event Action<PeerAnnouncement>? PeerUpdated;

    public async Task StartAsync(IEnumerable<NetworkAdapterInfo> adapters, Func<NetworkAdapterInfo, PeerAnnouncement> createAnnouncement)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            LoadKnownPeers();
            PruneKnownPeers();
            RefreshNeighborTargets();
            _createAnnouncement = createAnnouncement;

            _cts = new CancellationTokenSource();
            _listener = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort)) { EnableBroadcast = true };

            foreach (var adapter in adapters)
            {
                if (!IPAddress.TryParse(adapter.IPv4, out var adapterIp))
                {
                    _logger.Warn($"Peer discovery skipped adapter '{adapter.Name}' due to invalid IPv4: {adapter.IPv4}");
                    continue;
                }

                try
                {
                    var sender = new UdpClient(new IPEndPoint(adapterIp, 0)) { EnableBroadcast = true };
                    var targets = BuildTargets(adapter);
                    lock (_senderGate)
                    {
                        _senders.Add(new SenderEntry(sender, adapter, targets));
                        if (!_localAdvertisedIps.Contains(adapter.IPv4))
                        {
                            _localAdvertisedIps.Add(adapter.IPv4);
                        }
                    }

                    _logger.Info($"Peer discovery sender configured for {adapter.Name} ({adapter.IPv4}/{adapter.Mask}) with {targets.Count} target(s).");
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Peer discovery sender init failed for adapter '{adapter.Name}': {ex.Message}");
                }
            }

            _receiveLoopTask = ReceiveLoopAsync(_listener, _cts.Token);
            _sendLoopTask = SendLoopAsync(createAnnouncement, _cts.Token);
            _logger.Info($"Peer discovery started on {_senders.Count} interface(s): " +
                string.Join(", ", _senders.Select(s => $"{s.Adapter.Name} [{s.Adapter.IPv4}]").Take(8)));
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

    private async Task StopCoreAsync()
    {
        var cts = _cts;
        var listener = _listener;
        SenderEntry[] senders;
        lock (_senderGate)
        {
            senders = _senders.ToArray();
            _senders.Clear();
            _localAdvertisedIps.Clear();
        }
        var tasks = new[] { _receiveLoopTask, _sendLoopTask }.Where(task => task is not null).Cast<Task>().ToArray();
        _cts = null;
        _listener = null;
        _receiveLoopTask = null;
        _sendLoopTask = null;
        lock (_peerGate)
        {
            _neighborTargets.Clear();
            _lastDirectedReplies.Clear();
        }
        _createAnnouncement = null;

        cts?.Cancel();
        listener?.Dispose();
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

    private List<DiscoveryTarget> BuildTargets(NetworkAdapterInfo adapter)
    {
        var result = new List<DiscoveryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (IPAddress.TryParse(adapter.Broadcast, out var broadcast))
        {
            AddTarget(new IPEndPoint(broadcast, DiscoveryPort), isProbe: false);
        }

        foreach (var ip in VirtualNetworkService.EnumerateProbeAddresses(adapter, MaxFullSubnetProbeSize))
        {
            var value = ip.ToString();
            if (IsLocalIp(value)) continue;
            AddTarget(new IPEndPoint(ip, DiscoveryPort), isProbe: true);
        }

        return result;

        void AddTarget(IPEndPoint endpoint, bool isProbe)
        {
            var key = endpoint.ToString();
            if (seen.Add(key))
            {
                result.Add(new DiscoveryTarget(endpoint, isProbe));
            }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await listener.ReceiveAsync(token);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var announcement = JsonSerializer.Deserialize<PeerAnnouncement>(json, _jsonOptions);
                if (announcement?.App == "MinecraftPortable" &&
                    announcement.ProtocolVersion == ProtocolVersion)
                {
                    var peerIp = ResolvePeerIp(announcement, result.RemoteEndPoint);
                    if (!string.IsNullOrWhiteSpace(peerIp))
                    {
                        announcement.VpnIp = peerIp;
                        RememberPeer(announcement);
                    }

                    PeerUpdated?.Invoke(announcement);
                    if (!string.IsNullOrWhiteSpace(peerIp))
                    {
                        await SendDirectedReplyAsync(peerIp, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Peer discovery receive failed: {ex.Message}");
            }
        }
    }

    private async Task SendLoopAsync(Func<NetworkAdapterInfo, PeerAnnouncement> createAnnouncement, CancellationToken token)
    {
        SenderEntry[] senderSnapshot;
        lock (_senderGate) senderSnapshot = _senders.ToArray();
        if (senderSnapshot.Length == 0)
        {
            _logger.Warn("Peer discovery has no valid senders; peer announcements are not broadcast.");
            return;
        }

        var sendInterval = BaseSendInterval;
        var tick = 0;
        while (!token.IsCancellationRequested)
        {
            tick++;
            var includeProbes = tick % 5 == 1;
            if (includeProbes)
            {
                RefreshNeighborTargets();
            }
            var encounteredFailure = false;
            try
            {
                foreach (var entry in senderSnapshot)
                {
                    try
                    {
                        var announcement = createAnnouncement(entry.Adapter);
                        announcement.VpnIp = entry.Adapter.IPv4;
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
                        foreach (var target in BuildCurrentTargets(entry, includeProbes))
                        {
                            await entry.Sender.SendAsync(bytes, target.Endpoint, token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (ObjectDisposedException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (SocketException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        encounteredFailure = true;
                        _logger.Warn($"Peer discovery send failed for adapter '{entry.Adapter.Name}': {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                encounteredFailure = true;
                _logger.Warn($"Peer discovery send loop failed: {ex.Message}");
            }

            if (encounteredFailure)
            {
                var jitter = TimeSpan.FromMilliseconds(_random.Next(150, 450));
                sendInterval = TimeSpan.FromMilliseconds(Math.Min(MaxSendInterval.TotalMilliseconds, sendInterval.TotalMilliseconds + 700));
                _logger.Info($"Peer discovery send issue detected; slowing heartbeat to {sendInterval.TotalSeconds:0.0}s.");
                await Task.Delay(sendInterval + jitter, token);
            }
            else
            {
                sendInterval = BaseSendInterval;
                var jitter = TimeSpan.FromMilliseconds(_random.Next(-120, 180));
                var delay = sendInterval + jitter;
                if (delay < TimeSpan.FromMilliseconds(600))
                {
                    delay = TimeSpan.FromMilliseconds(600);
                }
                await Task.Delay(delay, token);
            }
        }
    }

    private List<DiscoveryTarget> BuildCurrentTargets(SenderEntry entry, bool includeProbes)
    {
        var targets = new List<DiscoveryTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in entry.Targets)
        {
            if (target.IsProbe && !includeProbes) continue;
            Add(target.Endpoint, target.IsProbe);
        }

        KnownPeerRecord[] knownPeers;
        IReadOnlyList<IPAddress> neighbors;
        lock (_peerGate)
        {
            knownPeers = _knownPeers.Values.ToArray();
            neighbors = _neighborTargets.TryGetValue(entry.Adapter.InterfaceIndex, out var values)
                ? values
                : Array.Empty<IPAddress>();
        }

        foreach (var knownPeer in knownPeers)
        {
            if (!IPAddress.TryParse(knownPeer.Ip, out var peerIp) || IsLocalIp(knownPeer.Ip)) continue;
            Add(new IPEndPoint(peerIp, DiscoveryPort), isProbe: false);
        }

        foreach (var neighbor in neighbors)
        {
            var value = neighbor.ToString();
            if (IsLocalIp(value)) continue;
            Add(new IPEndPoint(neighbor, DiscoveryPort), isProbe: false);
        }

        return targets;

        void Add(IPEndPoint endpoint, bool isProbe)
        {
            if (seen.Add(endpoint.ToString())) targets.Add(new DiscoveryTarget(endpoint, isProbe));
        }
    }

    private void RefreshNeighborTargets()
    {
        try
        {
            var snapshot = _neighborService.GetIPv4Neighbors();
            lock (_peerGate)
            {
                _neighborTargets.Clear();
                foreach (var pair in snapshot)
                {
                    _neighborTargets[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastNeighborWarning < TimeSpan.FromMinutes(1)) return;
            _lastNeighborWarning = now;
            _logger.Warn($"Could not refresh network neighbors: {ex.Message}");
        }
    }

    private async Task SendDirectedReplyAsync(string peerIp, CancellationToken token)
    {
        if (IsLocalIp(peerIp) || !IPAddress.TryParse(peerIp, out var targetIp)) return;
        var factory = _createAnnouncement;
        if (factory is null) return;

        var now = DateTimeOffset.UtcNow;
        lock (_peerGate)
        {
            if (_lastDirectedReplies.TryGetValue(peerIp, out var previous) &&
                now - previous < TimeSpan.FromSeconds(3))
            {
                return;
            }
            _lastDirectedReplies[peerIp] = now;
        }

        SenderEntry[] senders;
        lock (_senderGate) senders = _senders.ToArray();
        var matchingSenders = senders.Where(entry =>
            IPAddress.TryParse(entry.Adapter.IPv4, out var adapterIp) &&
            IPAddress.TryParse(entry.Adapter.Mask, out var mask) &&
            VirtualNetworkService.IsInSameNetwork(targetIp, adapterIp, mask)).ToArray();
        foreach (var entry in matchingSenders.Length > 0 ? matchingSenders : senders)
        {
            try
            {
                var announcement = factory(entry.Adapter);
                announcement.VpnIp = entry.Adapter.IPv4;
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));
                await entry.Sender.SendAsync(bytes, new IPEndPoint(targetIp, DiscoveryPort), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                _logger.Warn($"Peer discovery directed reply failed for {peerIp}: {ex.Message}");
            }
        }
    }

    private static string ResolvePeerIp(PeerAnnouncement announcement, IPEndPoint remoteEndPoint)
    {
        if (IPAddress.TryParse(announcement.VpnIp, out var announcedIp) &&
            announcedIp.AddressFamily == AddressFamily.InterNetwork)
        {
            return announcedIp.ToString();
        }

        return remoteEndPoint.Address.AddressFamily == AddressFamily.InterNetwork
            ? remoteEndPoint.Address.ToString()
            : string.Empty;
    }

    private void RememberPeer(PeerAnnouncement announcement)
    {
        if (string.IsNullOrWhiteSpace(announcement.VpnIp) || IsLocalIp(announcement.VpnIp))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        bool shouldPersist;
        lock (_peerGate)
        {
            var isNew = !_knownPeers.TryGetValue(announcement.VpnIp, out var peer);
            peer ??= new KnownPeerRecord { Ip = announcement.VpnIp };
            peer.IdentityId = announcement.IdentityId;
            peer.PlayerName = announcement.PlayerName;
            peer.NetworkType = VirtualNetworkService.NormalizeNetworkType(announcement.NetworkType);
            peer.LastSeenUtc = now;
            _knownPeers[announcement.VpnIp] = peer;
            _knownPeersDirty = true;
            shouldPersist = isNew || now - _lastKnownPeerSave > TimeSpan.FromMinutes(5);
        }

        if (shouldPersist)
        {
            PersistKnownPeers(force: true);
        }
    }

    private void LoadKnownPeers()
    {
        try
        {
            var peers = new List<KnownPeerRecord>();
            if (File.Exists(_paths.NetworkPeersFile))
            {
                var json = File.ReadAllText(_paths.NetworkPeersFile);
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    peers = JsonSerializer.Deserialize<List<KnownPeerRecord>>(json, _jsonOptions) ?? new();
                }
                else if (document.RootElement.TryGetProperty("peers", out var peerArray) &&
                         peerArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in peerArray.EnumerateArray())
                    {
                        if (element.TryGetProperty("endpoints", out _))
                        {
                            var identity = element.Deserialize<KnownPeerIdentityRecord>(_jsonOptions);
                            if (identity is null) continue;
                            peers.AddRange(identity.Endpoints.Select(endpoint => new KnownPeerRecord
                            {
                                Ip = endpoint.Ip,
                                IdentityId = identity.IdentityId,
                                PlayerName = identity.PlayerName,
                                NetworkType = endpoint.NetworkType,
                                LastSeenUtc = endpoint.LastSeenUtc
                            }));
                        }
                        else
                        {
                            var legacyPeer = element.Deserialize<KnownPeerRecord>(_jsonOptions);
                            if (legacyPeer is not null) peers.Add(legacyPeer);
                        }
                    }
                }
            }

            lock (_peerGate)
            {
                _knownPeers.Clear();
                foreach (var peer in peers)
                {
                    if (string.IsNullOrWhiteSpace(peer.Ip) || !IPAddress.TryParse(peer.Ip, out _)) continue;
                    if (DateTimeOffset.UtcNow - peer.LastSeenUtc > KnownPeerTtl) continue;
                    _knownPeers[peer.Ip] = peer;
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
                _knownPeers.Remove(peer.Ip);
                _knownPeersDirty = true;
            }

            foreach (var peer in _knownPeers.Values.OrderByDescending(peer => peer.LastSeenUtc).Skip(MaxKnownPeers).ToArray())
            {
                _knownPeers.Remove(peer.Ip);
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
                        .GroupBy(
                            peer => string.IsNullOrWhiteSpace(peer.IdentityId) ? $"endpoint:{peer.Ip}" : peer.IdentityId,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(group => new KnownPeerIdentityRecord
                        {
                            IdentityId = group.Key.StartsWith("endpoint:", StringComparison.Ordinal) ? "" : group.Key,
                            PlayerName = group.OrderByDescending(peer => peer.LastSeenUtc).First().PlayerName,
                            Endpoints = group.Select(peer => new KnownPeerEndpointRecord
                            {
                                Ip = peer.Ip,
                                NetworkType = peer.NetworkType,
                                LastSeenUtc = peer.LastSeenUtc
                            }).ToList()
                        })
                        .ToList()
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

    public bool IsLocalIp(string ip)
    {
        lock (_senderGate) return _localAdvertisedIps.Contains(ip);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private sealed record SenderEntry(UdpClient Sender, NetworkAdapterInfo Adapter, IReadOnlyList<DiscoveryTarget> Targets);
    private sealed record DiscoveryTarget(IPEndPoint Endpoint, bool IsProbe);

    private sealed class KnownPeerRecord
    {
        public string Ip { get; set; } = "";
        public string IdentityId { get; set; } = "";
        public string PlayerName { get; set; } = "";
        public string NetworkType { get; set; } = "Unknown";
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class KnownPeerCache
    {
        public int SchemaVersion { get; set; } = 2;
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
        public string Ip { get; set; } = "";
        public string NetworkType { get; set; } = "Unknown";
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
