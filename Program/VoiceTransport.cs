using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public sealed class VoiceTransport : IAsyncDisposable, IDisposable
{
    public const int VoicePort = 35657;
    private readonly Logger _logger;
    private readonly object _gate = new();
    private readonly List<TransportSocket> _sockets = [];
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;

    public VoiceTransport(Logger logger)
    {
        _logger = logger;
    }

    public void StartListening(
        IPAddress listenAddress,
        int port,
        Func<IPEndPoint, byte[], Task> onPacketReceived,
        bool trafficProtectionEnabled = true)
    {
        StopAsync().AsTask().GetAwaiter().GetResult();
        var cts = new CancellationTokenSource();
        var addresses = listenAddress.Equals(IPAddress.Any) || listenAddress.Equals(IPAddress.IPv6Any)
            ? new[] { IPAddress.Any, IPAddress.IPv6Any }
            : new[] { listenAddress };
        var configured = new List<TransportSocket>();
        try
        {
            foreach (var address in addresses)
            {
                try
                {
                    var udp = new UdpClient(address.AddressFamily);
                    if (address.AddressFamily == AddressFamily.InterNetworkV6) udp.Client.DualMode = false;
                    udp.Client.Bind(new IPEndPoint(address, port));
                    udp.Client.SendBufferSize = 512 * 1024;
                    udp.Client.ReceiveBufferSize = 512 * 1024;
                    var qos = VoiceQosSession.AttachBestEffort(udp.Client, _logger, trafficProtectionEnabled);
                    var receiveTask = ReceiveLoopAsync(udp, onPacketReceived, cts.Token);
                    configured.Add(new TransportSocket(udp, qos, receiveTask));
                }
                catch (SocketException ex)
                {
                    _logger.Warn($"Voice {address.AddressFamily} listener is unavailable: {ex.Message}");
                }
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

    public void SetTrafficProtectionEnabled(bool enabled)
    {
        TransportSocket[] sockets;
        lock (_gate) sockets = _sockets.ToArray();
        foreach (var socket in sockets)
        {
            socket.Qos.SetEnabled(enabled);
        }
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

    public async Task SendAsync(IEnumerable<IPEndPoint> targets, byte[] payload, CancellationToken token)
    {
        TransportSocket[] sockets;
        lock (_gate) sockets = _sockets.ToArray();
        if (payload.Length == 0 || sockets.Length == 0) return;

        foreach (var target in targets.Distinct())
        {
            var transport = sockets.FirstOrDefault(socket =>
                socket.Udp.Client.AddressFamily == target.AddressFamily);
            if (transport is null) continue;
            try
            {
                transport.Qos.AddDestination(target);
                await transport.Udp.SendAsync(payload, target, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                WarnThrottled($"Voice send to {target.Address} failed: {ex.Message}");
            }
        }
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

    private sealed record TransportSocket(UdpClient Udp, VoiceQosSession Qos, Task ReceiveTask);
}

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
