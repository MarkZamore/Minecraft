using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public sealed class VoiceTransport : IAsyncDisposable, IDisposable
{
    public const int VoicePort = 35657;
    private readonly Logger _logger;
    private readonly VirtualNetworkService _network;
    private readonly object _gate = new();
    private readonly List<TransportSocket> _sockets = [];
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;

    public VoiceTransport(Logger logger, VirtualNetworkService network)
    {
        _logger = logger;
        _network = network;
    }

    public void StartListening(
        IPAddress listenAddress,
        int port,
        Func<IPEndPoint, byte[], Task> onPacketReceived)
    {
        StopAsync().AsTask().GetAwaiter().GetResult();
        var cts = new CancellationTokenSource();
        var useNetworkEndpoints = listenAddress.Equals(IPAddress.Any) ||
                                  listenAddress.Equals(IPAddress.IPv6Any);
        var endpoints = useNetworkEndpoints
            ? _network.GetSnapshot().Endpoints
                .GroupBy(endpoint => endpoint.NetworkAddress, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray()
            : Array.Empty<NetworkEndpointInfo>();
        var configured = new List<TransportSocket>();
        try
        {
            if (useNetworkEndpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    TryConfigureSocket(endpoint, port, onPacketReceived, cts, configured);
                }
            }
            else
            {
                TryConfigureSocket(listenAddress, port, onPacketReceived, cts, configured);
            }
            if (configured.Count == 0)
            {
                throw new SocketException((int)SocketError.AddressNotAvailable);
            }
            lock (_gate)
            {
                _cts = cts;
                _sockets.AddRange(configured);
            }
        }
        catch
        {
            cts.Cancel();
            foreach (var socket in configured)
            {
                socket.Qos.Dispose();
                socket.Udp.Dispose();
            }
            cts.Dispose();
            throw;
        }
    }

    private void TryConfigureSocket(
        NetworkEndpointInfo endpoint,
        int port,
        Func<IPEndPoint, byte[], Task> onPacketReceived,
        CancellationTokenSource cts,
        ICollection<TransportSocket> configured)
    {
        try
        {
            var udp = _network.CreateBoundUdpClient(endpoint, port, reuseAddress: true);
            ConfigureBuffers(udp);
            var qos = VoiceQosSession.AttachBestEffort(udp.Client, _logger);
            var receiveTask = ReceiveLoopAsync(udp, onPacketReceived, cts.Token);
            configured.Add(new TransportSocket(
                udp,
                qos,
                receiveTask,
                endpoint.NetworkAddress,
                endpoint.ProviderId));
        }
        catch (SocketException ex)
        {
            _logger.Warn($"Voice listener on {endpoint.NetworkAddress} is unavailable: {ex.Message}");
        }
    }

    private void TryConfigureSocket(
        IPAddress address,
        int port,
        Func<IPEndPoint, byte[], Task> onPacketReceived,
        CancellationTokenSource cts,
        ICollection<TransportSocket> configured)
    {
        try
        {
            var udp = new UdpClient(address.AddressFamily);
            if (address.AddressFamily == AddressFamily.InterNetworkV6) udp.Client.DualMode = false;
            udp.Client.Bind(new IPEndPoint(address, port));
            ConfigureBuffers(udp);
            var qos = VoiceQosSession.AttachBestEffort(udp.Client, _logger);
            var receiveTask = ReceiveLoopAsync(udp, onPacketReceived, cts.Token);
            configured.Add(new TransportSocket(udp, qos, receiveTask, address.ToString(), ""));
        }
        catch (SocketException ex)
        {
            _logger.Warn($"Voice listener on {address} is unavailable: {ex.Message}");
        }
    }

    private static void ConfigureBuffers(UdpClient udp)
    {
        udp.Client.SendBufferSize = 512 * 1024;
        udp.Client.ReceiveBufferSize = 512 * 1024;
    }

    private async Task ReceiveLoopAsync(
        UdpClient udp,
        Func<IPEndPoint, byte[], Task> onPacketReceived,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(token).ConfigureAwait(false);
                await onPacketReceived(result.RemoteEndPoint, result.Buffer).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when ((ex is ObjectDisposedException or SocketException) && token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WarnThrottled("Voice receive failed: " + ex.Message);
                try
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public Task SendAsync(IEnumerable<IPEndPoint> targets, byte[] payload, CancellationToken token) =>
        SendAsync(targets.Select(target => new VoiceRouteTarget(target, "")), payload, token);

    public async Task SendAsync(
        IEnumerable<VoiceRouteTarget> targets,
        byte[] payload,
        CancellationToken token)
    {
        TransportSocket[] sockets;
        lock (_gate) sockets = _sockets.ToArray();
        if (payload.Length == 0 || sockets.Length == 0) return;

        foreach (var target in targets.Distinct())
        {
            var transport = SelectTransport(sockets, target);
            if (transport is null) continue;
            try
            {
                transport.Qos.AddDestination(target.EndPoint);
                await transport.Udp.SendAsync(payload, target.EndPoint, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                WarnThrottled($"Voice send to {target.EndPoint.Address} failed: {ex.Message}");
            }
        }
    }

    private TransportSocket? SelectTransport(
        IReadOnlyList<TransportSocket> sockets,
        VoiceRouteTarget target)
    {
        var family = target.EndPoint.AddressFamily;
        var familySockets = sockets
            .Where(socket => socket.Udp.Client.AddressFamily == family)
            .ToArray();
        if (familySockets.Length == 0) return null;

        if (!string.IsNullOrWhiteSpace(target.ProviderId))
        {
            var providerSocket = familySockets.FirstOrDefault(socket =>
                string.Equals(socket.ProviderId, target.ProviderId, StringComparison.OrdinalIgnoreCase));
            if (providerSocket is not null) return providerSocket;
            return null;
        }

        var selected = _network.SelectLocalEndpoint(target.EndPoint.Address);
        if (selected is not null)
        {
            var selectedSocket = familySockets.FirstOrDefault(socket =>
                string.Equals(socket.LocalAddress, selected.NetworkAddress, StringComparison.OrdinalIgnoreCase));
            if (selectedSocket is not null) return selectedSocket;
        }
        return familySockets.FirstOrDefault();
    }

    public async ValueTask StopAsync()
    {
        CancellationTokenSource? cts;
        TransportSocket[] sockets;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            sockets = _sockets.ToArray();
            _sockets.Clear();
        }
        cts?.Cancel();
        foreach (var socket in sockets)
        {
            socket.Qos.Dispose();
            socket.Udp.Dispose();
        }
        if (sockets.Length > 0)
        {
            try
            {
                await Task.WhenAll(sockets.Select(socket => socket.ReceiveTask)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
        cts?.Dispose();
    }

    private void WarnThrottled(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastWarningAt < TimeSpan.FromSeconds(15)) return;
        _lastWarningAt = now;
        _logger.Warn(message);
    }

    public void Dispose()
    {
        StopAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private sealed record TransportSocket(
        UdpClient Udp,
        VoiceQosSession Qos,
        Task ReceiveTask,
        string LocalAddress,
        string ProviderId);
}

public sealed record VoiceRouteTarget(IPEndPoint EndPoint, string ProviderId);

public sealed record VoicePeerCandidate(string PeerId, string Address, string ProviderId);

public sealed class VoiceRuntimeOptions
{
    public int Port { get; init; } = VoiceTransport.VoicePort;
    public IPAddress ListenAddress { get; init; } = IPAddress.IPv6Any;

    public void Validate()
    {
        if (Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (ListenAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException("Voice listen address must be IPv4 or IPv6.", nameof(ListenAddress));
        }
    }
}
