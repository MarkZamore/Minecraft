using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Minecraft;

public sealed class LanAdvertisementService : IAsyncDisposable
{
    public const int MinecraftLanDiscoveryPort = 4445;
    private static readonly IPAddress MinecraftLanMulticast = IPAddress.Parse("224.0.2.60");
    private static readonly TimeSpan AdvertisementInterval = TimeSpan.FromMilliseconds(1500);
    private readonly Logger _logger;
    private readonly LanRelayService _relay;
    private readonly VirtualNetworkService _network;
    private readonly PeerRouteResolver _routes;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, UdpClient> _senders = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private LanAdvertisementSnapshot _snapshot = LanAdvertisementSnapshot.Empty;
    private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;

    public LanAdvertisementService(
        Logger logger,
        LanRelayService relay,
        VirtualNetworkService network,
        PeerRouteResolver routes)
    {
        _logger = logger;
        _relay = relay;
        _network = network;
        _routes = routes;
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = RunAsync(_cts.Token);
    }

    public void Update(
        int? localPort,
        string localSessionId,
        string motd,
        IEnumerable<NetworkEndpointInfo> endpoints,
        IEnumerable<PeerViewModel> peers)
    {
        var endpointSnapshots = endpoints.ToArray();
        var preferredProviderId = endpointSnapshots
            .FirstOrDefault(endpoint => endpoint.IsPreferredNetwork)?.ProviderId ??
            endpointSnapshots.FirstOrDefault(endpoint =>
                !string.IsNullOrWhiteSpace(endpoint.ProviderId))?.ProviderId;
        var peerSnapshots = peers.Select(peer =>
        {
            var peerEndpoints = _routes.GetSendCandidates(peer.IdentityId, preferredProviderId);
            return new RemoteLanPeer(
                peer.IdentityId,
                peer.PlayerName,
                peer.IsHost,
                peer.ServerPort,
                peer.LanSessionId,
                peerEndpoints);
        }).ToArray();
        _relay.SetHostSession(localPort, localSessionId);
        lock (_stateGate)
        {
            _snapshot = new LanAdvertisementSnapshot(
                localPort is > 0 and <= 65535 ? localPort : null,
                localSessionId?.Trim() ?? "",
                SanitizeMotd(motd),
                endpointSnapshots,
                peerSnapshots);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await SendCurrentAdvertisementsAsync(token).ConfigureAwait(false);
                await Task.Delay(AdvertisementInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task SendCurrentAdvertisementsAsync(CancellationToken token)
    {
        LanAdvertisementSnapshot snapshot;
        lock (_stateGate) snapshot = _snapshot;

        if (snapshot.LocalPort is not null)
        {
            var payload = BuildPayload(snapshot.Motd, snapshot.LocalPort.Value);
            foreach (var peer in snapshot.Peers)
            {
                var sent = false;
                foreach (var candidate in peer.Endpoints.Where(endpoint =>
                             ParseAddress(endpoint.Address)?.AddressFamily == AddressFamily.InterNetwork))
                {
                    if (!IPAddress.TryParse(candidate.Address, out var target)) continue;
                    var endpoint = _network.SelectLocalEndpoint(target, candidate.ProviderId);
                    if (endpoint is null ||
                        endpoint.AddressFamily != AddressFamily.InterNetwork ||
                        !IPAddress.TryParse(endpoint.NetworkAddress, out var localAddress))
                    {
                        continue;
                    }
                    try
                    {
                        await GetOrCreateSender(localAddress).SendAsync(
                            payload,
                            new IPEndPoint(target, MinecraftLanDiscoveryPort),
                            token).ConfigureAwait(false);
                        sent = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                    {
                        WarnThrottled($"Minecraft LAN announcement to {target} failed: {ex.Message}");
                    }
                }
                if (!sent && peer.Endpoints.Any(endpoint =>
                        ParseAddress(endpoint.Address)?.AddressFamily == AddressFamily.InterNetwork))
                {
                    WarnThrottled($"Minecraft LAN announcement has no valid IPv4 route to {peer.PlayerName}.");
                }
            }
        }

        var retainedRelays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in snapshot.Peers.Where(peer => peer.IsHost && peer.ServerPort is > 0 and <= 65535))
        {
            var endpoints = peer.Endpoints
                .Where(endpoint => ParseAddress(endpoint.Address) is not null)
                .ToArray();
            if (endpoints.Length == 0) continue;
            if (endpoints.Any(endpoint =>
                    ParseAddress(endpoint.Address)?.AddressFamily == AddressFamily.InterNetwork)) continue;
            try
            {
                var relay = _relay.GetOrCreateClientRelay(
                    peer.IdentityId,
                    peer.LanSessionId,
                    endpoints,
                    peer.ServerPort);
                retainedRelays.Add(relay.Key);
                var payload = BuildPayload(SanitizeMotd(peer.PlayerName), relay.LocalPort);
                var sender = GetOrCreateSender(IPAddress.Loopback);
                await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, MinecraftLanDiscoveryPort), token).ConfigureAwait(false);
                await sender.SendAsync(payload, new IPEndPoint(MinecraftLanMulticast, MinecraftLanDiscoveryPort), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                WarnThrottled($"Minecraft LAN relay advertisement failed: {ex.Message}");
            }
        }
        _relay.RetainClientRelays(retainedRelays);
    }

    private UdpClient GetOrCreateSender(IPAddress localAddress)
    {
        var key = localAddress.ToString();
        lock (_stateGate)
        {
            if (_senders.TryGetValue(key, out var existing)) return existing;
            var sender = new UdpClient(new IPEndPoint(localAddress, 0));
            if (localAddress.Equals(IPAddress.Loopback))
            {
                sender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
                sender.MulticastLoopback = true;
            }
            _senders[key] = sender;
            return sender;
        }
    }

    private void WarnThrottled(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastWarningAt < TimeSpan.FromSeconds(30)) return;
        _lastWarningAt = now;
        _logger.Warn(message);
    }

    private static byte[] BuildPayload(string motd, int port) =>
        Encoding.UTF8.GetBytes($"[MOTD]{motd}[/MOTD][AD]{port}[/AD]");

    private static IPAddress? ParseAddress(string? value) =>
        IPAddress.TryParse(value, out var address) ? address : null;

    private static string SanitizeMotd(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "Minecraft LAN" : value.Trim();
        return result.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        var cts = _cts;
        var task = _loopTask;
        _cts = null;
        _loopTask = null;
        cts?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        cts?.Dispose();
        lock (_stateGate)
        {
            foreach (var sender in _senders.Values) sender.Dispose();
            _senders.Clear();
            _snapshot = LanAdvertisementSnapshot.Empty;
        }
    }

    private sealed record RemoteLanPeer(
        string IdentityId,
        string PlayerName,
        bool IsHost,
        int ServerPort,
        string LanSessionId,
        IReadOnlyList<PeerCandidateEndpoint> Endpoints);

    private sealed record LanAdvertisementSnapshot(
        int? LocalPort,
        string LocalSessionId,
        string Motd,
        IReadOnlyList<NetworkEndpointInfo> Endpoints,
        IReadOnlyList<RemoteLanPeer> Peers)
    {
        public static LanAdvertisementSnapshot Empty { get; } = new(null, "", "Minecraft LAN", [], []);
    }
}
