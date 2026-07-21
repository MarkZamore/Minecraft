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
    private readonly VirtualNetworkService _network;
    private readonly PeerRouteResolver _routes;
    private readonly ConcurrentDictionary<string, ClientRelay> _clientRelays = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private int _hostPort;
    private string _hostSessionId = "";

    public LanRelayService(Logger logger, VirtualNetworkService network, PeerRouteResolver routes)
    {
        _logger = logger;
        _network = network;
        _routes = routes;
    }

    public void SetHostSession(int? port, string? sessionId)
    {
        Volatile.Write(ref _hostPort, port is > 0 and <= 65535 ? port.Value : 0);
        Volatile.Write(ref _hostSessionId, sessionId?.Trim() ?? "");
    }

    public ClientLanRelayInfo GetOrCreateClientRelay(
        string peerId,
        string lanSessionId,
        IReadOnlyList<PeerCandidateEndpoint> endpoints,
        int remoteLanPort)
    {
        if (remoteLanPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(remoteLanPort));
        var targets = endpoints
            .Select(endpoint => IPAddress.TryParse(endpoint.Address, out var address)
                ? new LanRelayTarget(address, endpoint.ProviderId, endpoint.InterfaceId)
                : null)
            .Where(target => target is not null)
            .Cast<LanRelayTarget>()
            .Distinct()
            .ToArray();
        if (targets.Length == 0) throw new ArgumentException("LAN relay has no valid peer endpoint.", nameof(endpoints));
        var key = BuildKey(peerId, lanSessionId);
        var relay = _clientRelays.GetOrAdd(
            key,
            _ => new ClientRelay(
                peerId,
                targets,
                remoteLanPort,
                lanSessionId,
                _logger,
                _jsonOptions,
                _network,
                _routes));
        relay.UpdateTargets(targets, remoteLanPort, lanSessionId);
        return new ClientLanRelayInfo(key, relay.LocalPort);
    }

    public async Task RetainClientRelaysAsync(IReadOnlySet<string> activeKeys)
    {
        var removed = new List<ClientRelay>();
        foreach (var pair in _clientRelays.ToArray())
        {
            if (activeKeys.Contains(pair.Key) || !_clientRelays.TryRemove(pair.Key, out var relay)) continue;
            removed.Add(relay);
        }
        foreach (var relay in removed) await relay.DisposeAsync().ConfigureAwait(false);
    }

    public async Task HandleIncomingAsync(Stream stream, byte[] initialFrame, CancellationToken token)
    {
        var request = PortableProtocol.Deserialize<LanRelayRequest>(initialFrame, _jsonOptions);
        var hostPort = Volatile.Read(ref _hostPort);
        var hostSessionId = Volatile.Read(ref _hostSessionId);
        if (request is null || request.Protocol != ProtocolName || request.ProtocolVersion != ProtocolVersion ||
            request.ServerPort is <= 0 or > 65535 || request.ServerPort != hostPort ||
            string.IsNullOrWhiteSpace(request.LanSessionId) ||
            !string.Equals(request.LanSessionId, hostSessionId, StringComparison.Ordinal))
        {
            await PortableProtocol.WriteJsonAsync(stream, new LanRelayReply
            {
                Ok = false,
                Message = "LAN session is not available."
            }, _jsonOptions, token).ConfigureAwait(false);
            return;
        }

        TcpClient? minecraft = null;
        var readySent = false;
        try
        {
            minecraft = await ConnectLocalMinecraftAsync(hostPort, token).ConfigureAwait(false);
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
        finally
        {
            minecraft?.Dispose();
        }
    }

    private static async Task<TcpClient> ConnectLocalMinecraftAsync(int port, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var client = new TcpClient(address.AddressFamily);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(address, port, attempt.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                client.Dispose();
                if (token.IsCancellationRequested) throw;
                lastError = ex;
            }
        }
        throw new IOException("Could not reach the local Minecraft listener.", lastError);
    }

    public async ValueTask DisposeAsync()
    {
        var relays = _clientRelays.Values.ToArray();
        _clientRelays.Clear();
        foreach (var relay in relays) await relay.DisposeAsync().ConfigureAwait(false);
    }

    private static string BuildKey(
        string peerId,
        string lanSessionId) =>
        string.IsNullOrWhiteSpace(lanSessionId)
            ? throw new ArgumentException("LAN relay requires a session id.", nameof(lanSessionId))
            : $"{peerId.Trim()}|{lanSessionId.Trim()}";

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
        private readonly string _peerId;
        private readonly object _targetGate = new();
        private IReadOnlyList<LanRelayTarget> _targets;
        private int _remoteLanPort;
        private string _lanSessionId;
        private readonly Logger _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly VirtualNetworkService _network;
        private readonly PeerRouteResolver _routes;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptTask;
        private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
        private long _nextClientTaskId;

        public ClientRelay(
            string peerId,
            IReadOnlyList<LanRelayTarget> targets,
            int remoteLanPort,
            string lanSessionId,
            Logger logger,
            JsonSerializerOptions jsonOptions,
            VirtualNetworkService network,
            PeerRouteResolver routes)
        {
            _peerId = peerId;
            _targets = targets;
            _remoteLanPort = remoteLanPort;
            _lanSessionId = lanSessionId;
            _logger = logger;
            _jsonOptions = jsonOptions;
            _network = network;
            _routes = routes;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptLoopAsync(_cts.Token);
        }

        public int LocalPort { get; }

        public void UpdateTargets(
            IReadOnlyList<LanRelayTarget> targets,
            int remoteLanPort,
            string lanSessionId)
        {
            lock (_targetGate)
            {
                _targets = targets.ToArray();
                _remoteLanPort = remoteLanPort;
                _lanSessionId = lanSessionId?.Trim() ?? "";
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    var taskId = Interlocked.Increment(ref _nextClientTaskId);
                    var task = HandleClientAsync(client, token);
                    _clientTasks[taskId] = task;
                    _ = ObserveClientTaskAsync(taskId, task);
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

        private async Task ObserveClientTaskAsync(long taskId, Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
            finally
            {
                _clientTasks.TryRemove(taskId, out _);
            }
        }

        private async Task HandleClientAsync(TcpClient localClient, CancellationToken token)
        {
            using (localClient)
            {
                var failures = new List<string>();
                IReadOnlyList<LanRelayTarget> targets;
                int remoteLanPort;
                string lanSessionId;
                lock (_targetGate)
                {
                    targets = _targets.ToArray();
                    remoteLanPort = _remoteLanPort;
                    lanSessionId = _lanSessionId;
                }

                foreach (var target in targets)
                {
                    try
                    {
                        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        connectCts.CancelAfter(TimeSpan.FromSeconds(4));
                        using var remoteClient = _network.CreateBoundTcpClient(target.Address, target.ProviderId);
                        await remoteClient.ConnectAsync(
                            target.Address,
                            WorldTransferService.TransferPort,
                            connectCts.Token).ConfigureAwait(false);
                        await using var remoteStream = remoteClient.GetStream();
                        await PortableProtocol.WriteJsonAsync(remoteStream, new LanRelayRequest
                        {
                            ServerPort = remoteLanPort,
                            LanSessionId = lanSessionId
                        }, _jsonOptions, connectCts.Token).ConfigureAwait(false);
                        var replyFrame = await PortableProtocol.ReadFrameAsync(
                            remoteStream,
                            connectCts.Token).ConfigureAwait(false);
                        var reply = PortableProtocol.Deserialize<LanRelayReply>(replyFrame, _jsonOptions);
                        if (reply is null || reply.Protocol != ProtocolName ||
                            reply.ProtocolVersion != ProtocolVersion || !reply.Ok)
                        {
                            throw new IOException(reply?.Message ?? "Remote LAN relay was rejected.");
                        }
                        _routes.MarkEndpointHealthy(_peerId, target.ToCandidate());
                        await RelayBidirectionalAsync(
                            localClient.GetStream(),
                            remoteStream,
                            token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
                    {
                        if (token.IsCancellationRequested) return;
                        _routes.MarkEndpointUnhealthy(_peerId, target.ToCandidate());
                        failures.Add($"{target.Address}: {ex.Message}");
                    }
                }
                _logger.Warn("Minecraft LAN relay failed: " + string.Join("; ", failures.Take(3)));
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
            var clientTasks = _clientTasks.Values.ToArray();
            if (clientTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(clientTasks).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
                {
                }
            }
            _cts.Dispose();
        }
    }

    private sealed class LanRelayRequest
    {
        public string Protocol { get; set; } = ProtocolName;
        public int ProtocolVersion { get; set; } = LanRelayService.ProtocolVersion;
        public int ServerPort { get; set; }
        public string LanSessionId { get; set; } = "";
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

internal sealed record LanRelayTarget(IPAddress Address, string ProviderId, string InterfaceId)
{
    public PeerCandidateEndpoint ToCandidate() => new()
    {
        Address = Address.ToString(),
        ProviderId = ProviderId,
        InterfaceId = InterfaceId,
        AddressFamily = Address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4"
    };
}
