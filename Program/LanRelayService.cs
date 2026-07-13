using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Minecraft;

public sealed class LanRelayService : IAsyncDisposable
{
    public const string ProtocolName = "MinecraftPortableLanRelay";
    public const int ProtocolVersion = 1;
    private readonly Logger _logger;
    private readonly ConcurrentDictionary<string, ClientRelay> _clientRelays = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private int _hostPort;

    public LanRelayService(Logger logger)
    {
        _logger = logger;
    }

    public void SetHostPort(int? port) =>
        Volatile.Write(ref _hostPort, port is > 0 and <= 65535 ? port.Value : 0);

    public ClientLanRelayInfo GetOrCreateClientRelay(IPAddress remoteAddress, int remoteLanPort)
    {
        if (remoteAddress.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new ArgumentException("A LAN relay is only needed for an IPv6 endpoint.", nameof(remoteAddress));
        }
        if (remoteLanPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(remoteLanPort));
        var key = BuildKey(remoteAddress, remoteLanPort);
        var relay = _clientRelays.GetOrAdd(key, _ => new ClientRelay(remoteAddress, remoteLanPort, _logger, _jsonOptions));
        return new ClientLanRelayInfo(key, relay.LocalPort);
    }

    public void RetainClientRelays(IReadOnlySet<string> activeKeys)
    {
        foreach (var pair in _clientRelays.ToArray())
        {
            if (activeKeys.Contains(pair.Key) || !_clientRelays.TryRemove(pair.Key, out var relay)) continue;
            _ = relay.DisposeAsync().AsTask();
        }
    }

    public async Task HandleIncomingAsync(Stream stream, byte[] initialFrame, CancellationToken token)
    {
        var request = PortableProtocol.Deserialize<LanRelayRequest>(initialFrame, _jsonOptions);
        var hostPort = Volatile.Read(ref _hostPort);
        if (request is null || request.Protocol != ProtocolName || request.ProtocolVersion != ProtocolVersion ||
            request.ServerPort is <= 0 or > 65535 || request.ServerPort != hostPort)
        {
            await PortableProtocol.WriteJsonAsync(stream, new LanRelayReply
            {
                Ok = false,
                Message = "LAN session is not available."
            }, _jsonOptions, token).ConfigureAwait(false);
            return;
        }

        using var minecraft = new TcpClient(AddressFamily.InterNetwork);
        var readySent = false;
        try
        {
            await minecraft.ConnectAsync(IPAddress.Loopback, hostPort, token).ConfigureAwait(false);
            await PortableProtocol.WriteJsonAsync(stream, new LanRelayReply { Ok = true }, _jsonOptions, token).ConfigureAwait(false);
            readySent = true;
            await RelayBidirectionalAsync(stream, minecraft.GetStream(), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _logger.Warn($"Incoming Minecraft LAN relay failed: {ex.Message}");
            try
            {
                if (!readySent)
                {
                    await PortableProtocol.WriteJsonAsync(stream, new LanRelayReply
                    {
                        Ok = false,
                        Message = "Could not reach the local LAN world."
                    }, _jsonOptions, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var relays = _clientRelays.Values.ToArray();
        _clientRelays.Clear();
        foreach (var relay in relays) await relay.DisposeAsync().ConfigureAwait(false);
    }

    private static string BuildKey(IPAddress address, int port) => $"{address}|{port}";

    private static async Task RelayBidirectionalAsync(Stream first, Stream second, CancellationToken token)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstToSecond = first.CopyToAsync(second, relayCts.Token);
        var secondToFirst = second.CopyToAsync(first, relayCts.Token);
        await Task.WhenAny(firstToSecond, secondToFirst).ConfigureAwait(false);
        relayCts.Cancel();
        try
        {
            await Task.WhenAll(firstToSecond, secondToFirst).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private sealed class ClientRelay : IAsyncDisposable
    {
        private readonly IPAddress _remoteAddress;
        private readonly int _remoteLanPort;
        private readonly Logger _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;

        public ClientRelay(
            IPAddress remoteAddress,
            int remoteLanPort,
            Logger logger,
            JsonSerializerOptions jsonOptions)
        {
            _remoteAddress = remoteAddress;
            _remoteLanPort = remoteLanPort;
            _logger = logger;
            _jsonOptions = jsonOptions;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(_cts.Token);
        }

        public int LocalPort { get; }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    _ = HandleClientAsync(client, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    if (!token.IsCancellationRequested) _logger.Warn($"Local Minecraft LAN relay listener failed: {ex.Message}");
                    break;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient localClient, CancellationToken token)
        {
            using (localClient)
            using (var remoteClient = new TcpClient(AddressFamily.InterNetworkV6))
            {
                try
                {
                    await remoteClient.ConnectAsync(_remoteAddress, WorldTransferService.TransferPort, token).ConfigureAwait(false);
                    await using var remoteStream = remoteClient.GetStream();
                    await PortableProtocol.WriteJsonAsync(remoteStream, new LanRelayRequest
                    {
                        ServerPort = _remoteLanPort
                    }, _jsonOptions, token).ConfigureAwait(false);
                    var replyFrame = await PortableProtocol.ReadFrameAsync(remoteStream, token).ConfigureAwait(false);
                    var reply = PortableProtocol.Deserialize<LanRelayReply>(replyFrame, _jsonOptions);
                    if (reply is null || reply.Protocol != ProtocolName || reply.ProtocolVersion != ProtocolVersion || !reply.Ok)
                    {
                        throw new IOException(reply?.Message ?? "Remote LAN relay was rejected.");
                    }
                    await RelayBidirectionalAsync(localClient.GetStream(), remoteStream, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
                {
                    if (!token.IsCancellationRequested) _logger.Warn($"Minecraft LAN relay to {_remoteAddress} failed: {ex.Message}");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
            _cts.Dispose();
        }
    }

    private sealed class LanRelayRequest
    {
        public string Protocol { get; set; } = ProtocolName;
        public int ProtocolVersion { get; set; } = LanRelayService.ProtocolVersion;
        public int ServerPort { get; set; }
    }

    private sealed class LanRelayReply
    {
        public string Protocol { get; set; } = ProtocolName;
        public int ProtocolVersion { get; set; } = LanRelayService.ProtocolVersion;
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }
}

public sealed record ClientLanRelayInfo(string Key, int LocalPort);
