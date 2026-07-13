using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public sealed class VoiceTransport : IAsyncDisposable, IDisposable
{
    public const int VoicePort = 35657;
    private readonly Logger _logger;
    private readonly object _gate = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private VoiceQosSession? _qos;
    private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;

    public VoiceTransport(Logger logger)
    {
        _logger = logger;
    }

    public void StartListening(
        IPAddress listenAddress,
        int port,
        Func<IPEndPoint, byte[], Task> onPacketReceived)
    {
        StopAsync().AsTask().GetAwaiter().GetResult();
        var cts = new CancellationTokenSource();
        var udp = new UdpClient(new IPEndPoint(listenAddress, port));
        udp.Client.SendBufferSize = 512 * 1024;
        udp.Client.ReceiveBufferSize = 512 * 1024;
        var qos = VoiceQosSession.AttachBestEffort(udp.Client, _logger);
        lock (_gate)
        {
            _cts = cts;
            _udp = udp;
            _receiveTask = ReceiveLoopAsync(udp, onPacketReceived, cts.Token);
            _qos = qos;
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
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex) when (token.IsCancellationRequested || ex.SocketErrorCode == SocketError.OperationAborted)
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
        UdpClient? udp;
        VoiceQosSession? qos;
        lock (_gate)
        {
            udp = _udp;
            qos = _qos;
        }
        if (udp is null || payload.Length == 0) return;

        foreach (var target in targets.Distinct())
        {
            try
            {
                qos?.AddDestination(target);
                await udp.SendAsync(payload, target, token).ConfigureAwait(false);
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
        UdpClient? udp;
        Task? task;
        VoiceQosSession? qos;
        lock (_gate)
        {
            cts = _cts;
            udp = _udp;
            task = _receiveTask;
            _cts = null;
            _udp = null;
            _receiveTask = null;
            qos = _qos;
            _qos = null;
        }

        cts?.Cancel();
        qos?.Dispose();
        udp?.Dispose();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
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
}

public sealed class VoiceRuntimeOptions
{
    public int Port { get; init; } = VoiceTransport.VoicePort;
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    public void Validate()
    {
        if (Port is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (ListenAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Voice listen address must be IPv4.", nameof(ListenAddress));
        }
    }
}
