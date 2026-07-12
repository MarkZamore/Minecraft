using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Minecraft;

public sealed class LanAdvertisementService : IAsyncDisposable
{
    public const int MinecraftLanDiscoveryPort = 4445;
    private static readonly TimeSpan AdvertisementInterval = TimeSpan.FromMilliseconds(1500);
    private readonly Logger _logger;
    private readonly object _stateGate = new();
    private readonly Dictionary<string, UdpClient> _senders = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private LanAdvertisementSnapshot _snapshot = LanAdvertisementSnapshot.Empty;
    private DateTimeOffset _lastWarningAt = DateTimeOffset.MinValue;

    public LanAdvertisementService(Logger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        if (_loopTask is not null) return;
        _cts = new CancellationTokenSource();
        _loopTask = RunAsync(_cts.Token);
    }

    public void Update(
        int? port,
        string motd,
        IEnumerable<NetworkAdapterInfo> adapters,
        IEnumerable<PeerViewModel> peers)
    {
        var targets = peers
            .SelectMany(peer => peer.NetworkEndpoints)
            .Select(endpoint => endpoint.Address)
            .Where(address => IPAddress.TryParse(address, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (_stateGate)
        {
            _snapshot = new LanAdvertisementSnapshot(
                port is > 0 and <= 65535 ? port : null,
                SanitizeMotd(motd),
                adapters.ToArray(),
                targets);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await SendCurrentAdvertisementAsync(token).ConfigureAwait(false);
                await Task.Delay(AdvertisementInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task SendCurrentAdvertisementAsync(CancellationToken token)
    {
        LanAdvertisementSnapshot snapshot;
        lock (_stateGate)
        {
            snapshot = _snapshot;
        }
        if (snapshot.Port is null || snapshot.Targets.Count == 0) return;

        var payload = Encoding.UTF8.GetBytes($"[MOTD]{snapshot.Motd}[/MOTD][AD]{snapshot.Port.Value}[/AD]");
        foreach (var targetText in snapshot.Targets)
        {
            if (!IPAddress.TryParse(targetText, out var targetIp)) continue;
            var matchingAdapters = snapshot.Adapters.Where(adapter =>
                IPAddress.TryParse(adapter.IPv4, out var adapterIp) &&
                IPAddress.TryParse(adapter.Mask, out var mask) &&
                VirtualNetworkService.IsInSameNetwork(targetIp, adapterIp, mask)).ToArray();
            var candidateAdapters = matchingAdapters.Length > 0 ? matchingAdapters : snapshot.Adapters;
            foreach (var adapter in candidateAdapters)
            {
                if (!IPAddress.TryParse(adapter.IPv4, out var adapterIp)) continue;

                try
                {
                    var sender = GetOrCreateSender(adapterIp);
                    await sender.SendAsync(
                        payload,
                        new IPEndPoint(targetIp, MinecraftLanDiscoveryPort),
                        token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    WarnThrottled($"Minecraft LAN announcement to {targetIp} failed: {ex.Message}");
                }
            }
        }
    }

    private UdpClient GetOrCreateSender(IPAddress localAddress)
    {
        var key = localAddress.ToString();
        lock (_stateGate)
        {
            if (_senders.TryGetValue(key, out var existing)) return existing;
            var sender = new UdpClient(new IPEndPoint(localAddress, 0));
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

    private static string SanitizeMotd(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "Minecraft LAN" : value.Trim();
        return result.Replace("[", "(", StringComparison.Ordinal)
            .Replace("]", ")", StringComparison.Ordinal);
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

    private sealed record LanAdvertisementSnapshot(
        int? Port,
        string Motd,
        IReadOnlyList<NetworkAdapterInfo> Adapters,
        IReadOnlyList<string> Targets)
    {
        public static LanAdvertisementSnapshot Empty { get; } = new(null, "Minecraft LAN", [], []);
    }
}
