using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Minecraft;

public sealed class VoiceTransport : IDisposable
{
    public const int VoicePort = 35657;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public event Action<IPEndPoint, byte[]>? PacketReceived;

    public void StartListening(int port, Func<IPEndPoint, byte[], Task> onPacketReceived)
    {
        Stop();

        _cts = new CancellationTokenSource();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port))
        {
            EnableBroadcast = true
        };

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync(_cts.Token);
                    PacketReceived?.Invoke(result.RemoteEndPoint, result.Buffer);
                    await onPacketReceived(result.RemoteEndPoint, result.Buffer);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(50, _cts.Token);
                }
            }
        }, _cts.Token);
    }

    public async Task SendAsync(IEnumerable<IPEndPoint> targets, byte[] payload, CancellationToken token)
    {
        if (_udp is null || payload.Length == 0) return;
        foreach (var target in targets)
        {
            try
            {
                if (target.Address.Equals(IPAddress.Loopback)) continue;
                await _udp.SendAsync(payload, payload.Length, target);
            }
            catch
            {
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _udp?.Dispose();
        _udp = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

}
