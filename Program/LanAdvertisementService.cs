using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Minecraft;

public sealed class LanAdvertisementService : IAsyncDisposable
{
    public const int MinecraftLanDiscoveryPort = 4445;
    private static readonly IPAddress MinecraftLanMulticast = IPAddress.Parse("224.0.2.60");
    private static readonly TimeSpan AdvertisementInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RelayStabilizationDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MissingSessionRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActiveEndpointWindow = TimeSpan.FromSeconds(40);
    private readonly Logger _logger;
    private readonly LanRelayService _relay;
    private readonly VirtualNetworkService _network;
    private readonly PeerRouteResolver _routes;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, UdpClient> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LanRouteState> _remoteSessions = new(StringComparer.OrdinalIgnoreCase);
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
        string worldName,
        string playerName,
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
                peer.LanWorldName,
                peerEndpoints);
        }).ToArray();
        _relay.SetHostSession(localPort, localSessionId);
        lock (_stateGate)
        {
            _snapshot = new LanAdvertisementSnapshot(
                localPort is > 0 and <= 65535 ? localPort : null,
                localSessionId?.Trim() ?? "",
                SanitizeMotd(worldName),
                SanitizeMotd(playerName),
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
        var now = DateTimeOffset.UtcNow;

        if (snapshot.LocalPort is not null)
        {
            var payload = BuildPayload(snapshot.WorldName, snapshot.PlayerName, snapshot.LocalPort.Value);
            foreach (var peer in snapshot.Peers)
            {
                var sent = false;
                foreach (var candidate in peer.Endpoints.Where(endpoint =>
                             now - endpoint.LastSeenUtc <= ActiveEndpointWindow &&
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
                        now - endpoint.LastSeenUtc <= ActiveEndpointWindow &&
                        ParseAddress(endpoint.Address)?.AddressFamily == AddressFamily.InterNetwork))
                {
                    WarnThrottled($"Minecraft LAN announcement has no valid IPv4 route to {peer.PlayerName}.");
                }
            }
        }

        var retainedRelays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleIdentities = snapshot.Peers
            .Select(peer => peer.IdentityId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localSupportsDirectIpv4 = SupportsDirectIpv4(snapshot.Endpoints);
        foreach (var peer in snapshot.Peers.Where(peer => peer.IsHost && peer.ServerPort is > 0 and <= 65535))
        {
            if (string.IsNullOrWhiteSpace(peer.LanSessionId)) continue;
            var sessionKey = BuildSessionKey(peer.IdentityId, peer.LanSessionId);
            retainedSessions.Add(sessionKey);
            var endpoints = peer.Endpoints
                .Where(endpoint => now - endpoint.LastSeenUtc <= ActiveEndpointWindow &&
                                   ParseAddress(endpoint.Address) is not null)
                .ToArray();
            if (endpoints.Length == 0) continue;
            var hasConfirmedIpv4 = endpoints.Any(endpoint =>
                endpoint.IsConfirmed &&
                ParseAddress(endpoint.Address)?.AddressFamily == AddressFamily.InterNetwork);
            var hasConfirmedIpv6 = endpoints.Any(endpoint =>
                endpoint.IsConfirmed &&
                ParseAddress(endpoint.Address)?.AddressFamily == AddressFamily.InterNetworkV6);

            LanRouteState routeState;
            lock (_stateGate)
            {
                if (!_remoteSessions.TryGetValue(sessionKey, out routeState!))
                {
                    routeState = new LanRouteState(now);
                    _remoteSessions.Add(sessionKey, routeState);
                }
                var previousMode = routeState.Mode;
                var currentMode = LanRoutePolicy.Observe(
                    routeState,
                    now,
                    localSupportsDirectIpv4,
                    hasConfirmedIpv4,
                    hasConfirmedIpv6,
                    RelayStabilizationDelay);
                if (previousMode != currentMode && currentMode == LanRouteMode.Direct)
                {
                    _logger.Info($"Minecraft LAN session {peer.LanSessionId} for {peer.PlayerName} selected direct IPv4.");
                }
                else if (previousMode != currentMode && currentMode == LanRouteMode.Relay)
                {
                    _logger.Info($"Minecraft LAN session {peer.LanSessionId} for {peer.PlayerName} selected IPv6 relay.");
                }
            }

            if (routeState.Mode != LanRouteMode.Relay) continue;
            try
            {
                var relay = _relay.GetOrCreateClientRelay(
                    peer.IdentityId,
                    peer.LanSessionId,
                    endpoints,
                    peer.ServerPort);
                routeState.RelayKey = relay.Key;
                retainedRelays.Add(relay.Key);
                var payload = BuildPayload(
                    SanitizeMotd(peer.WorldName),
                    SanitizeMotd(peer.PlayerName),
                    relay.LocalPort);
                var sender = GetOrCreateSender(IPAddress.Loopback);
                await sender.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, MinecraftLanDiscoveryPort), token).ConfigureAwait(false);
                await sender.SendAsync(payload, new IPEndPoint(MinecraftLanMulticast, MinecraftLanDiscoveryPort), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                WarnThrottled($"Minecraft LAN relay advertisement failed: {ex.Message}");
            }
        }
        lock (_stateGate)
        {
            foreach (var pair in _remoteSessions.ToArray())
            {
                if (retainedSessions.Contains(pair.Key)) continue;
                var separator = pair.Key.IndexOf('|');
                var identityId = separator > 0 ? pair.Key[..separator] : pair.Key;
                var explicitlyClosed = visibleIdentities.Contains(identityId);
                if (explicitlyClosed || now - pair.Value.LastSeenUtc > MissingSessionRetention)
                {
                    _remoteSessions.Remove(pair.Key);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(pair.Value.RelayKey))
                {
                    retainedRelays.Add(pair.Value.RelayKey);
                }
            }
        }
        await _relay.RetainClientRelaysAsync(retainedRelays).ConfigureAwait(false);
    }

    private static bool SupportsDirectIpv4(IReadOnlyList<NetworkEndpointInfo> endpoints)
    {
        var preferredProvider = endpoints.FirstOrDefault(endpoint => endpoint.IsPreferredNetwork)?.ProviderId;
        return endpoints.Any(endpoint =>
            endpoint.AddressFamily == AddressFamily.InterNetwork &&
            (!string.IsNullOrWhiteSpace(preferredProvider)
                ? string.Equals(endpoint.ProviderId, preferredProvider, StringComparison.OrdinalIgnoreCase)
                : endpoint.IsPhysical));
    }

    private static string BuildSessionKey(string identityId, string sessionId) =>
        $"{identityId.Trim()}|{sessionId.Trim()}";

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

    internal static byte[] BuildPayload(string worldName, string playerName, int port)
    {
        var normalizedWorldName = string.Equals(
            worldName.Trim(),
            "Minecraft LAN",
            StringComparison.OrdinalIgnoreCase)
            ? ""
            : SanitizeMotd(worldName);
        var metadata = string.Join(":",
            "MinecraftPortable",
            EncodeMetadata(normalizedWorldName),
            EncodeMetadata(SanitizeMotd(playerName)));
        return Encoding.UTF8.GetBytes($"[MOTD]{metadata}[/MOTD][AD]{port}[/AD]");
    }

    private static string EncodeMetadata(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
            _remoteSessions.Clear();
            _snapshot = LanAdvertisementSnapshot.Empty;
        }
    }

    private sealed record RemoteLanPeer(
        string IdentityId,
        string PlayerName,
        bool IsHost,
        int ServerPort,
        string LanSessionId,
        string WorldName,
        IReadOnlyList<PeerCandidateEndpoint> Endpoints);

    private sealed record LanAdvertisementSnapshot(
        int? LocalPort,
        string LocalSessionId,
        string WorldName,
        string PlayerName,
        IReadOnlyList<NetworkEndpointInfo> Endpoints,
        IReadOnlyList<RemoteLanPeer> Peers)
    {
        public static LanAdvertisementSnapshot Empty { get; } =
            new(null, "", "Minecraft LAN", "Minecraft", [], []);
    }

}
