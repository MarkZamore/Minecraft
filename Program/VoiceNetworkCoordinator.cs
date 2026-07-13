using System.Diagnostics;

namespace Minecraft;

public sealed class VoiceNetworkCoordinator
{
    private readonly object _gate = new();
    private bool _joined;
    private bool _trafficProtectionEnabled = true;
    private int _connectedPeers;
    private double _lossPercent;
    private double _jitterMs;
    private double _rttMs;

    public bool ShouldProtectVoice
    {
        get
        {
            lock (_gate) return _joined && _connectedPeers > 0 && _trafficProtectionEnabled;
        }
    }

    public VoiceNetworkSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new VoiceNetworkSnapshot(
                    _joined,
                    _trafficProtectionEnabled,
                    _connectedPeers,
                    _lossPercent,
                    _jitterMs,
                    _rttMs);
            }
        }
    }

    public void SetJoined(bool joined)
    {
        lock (_gate)
        {
            _joined = joined;
            if (!joined)
            {
                _connectedPeers = 0;
                _lossPercent = 0;
                _jitterMs = 0;
                _rttMs = 0;
            }
        }
    }

    public void SetTrafficProtectionEnabled(bool enabled)
    {
        lock (_gate)
        {
            _trafficProtectionEnabled = enabled;
        }
    }

    public void ReportQuality(int connectedPeers, double lossPercent, double jitterMs, double rttMs)
    {
        lock (_gate)
        {
            _connectedPeers = Math.Max(0, connectedPeers);
            _lossPercent = Math.Clamp(lossPercent, 0, 100);
            _jitterMs = Math.Max(0, jitterMs);
            _rttMs = Math.Max(0, rttMs);
        }
    }

    public VoiceTransferLimiter CreateTransferLimiter() => new(this);
}

public readonly record struct VoiceNetworkSnapshot(
    bool IsJoined,
    bool IsTrafficProtectionEnabled,
    int ConnectedPeers,
    double LossPercent,
    double JitterMs,
    double RoundTripMs)
{
    public bool IsDegraded => LossPercent >= 4 || JitterMs >= 60 || RoundTripMs >= 250;
}

public sealed class VoiceTransferLimiter : IDisposable
{
    public const int TransferBlockSize = 64 * 1024;
    private const double InitialBytesPerSecond = 1024d * 1024d;
    private const double MinimumBytesPerSecond = 128d * 1024d;
    private const double MaximumBytesPerSecond = 64d * 1024d * 1024d;
    private static readonly TimeSpan AdjustmentInterval = TimeSpan.FromSeconds(2);
    private readonly VoiceNetworkCoordinator _coordinator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _rate = InitialBytesPerSecond;
    private TimeSpan _lastAdjustment;
    private TimeSpan _nextWriteAt;

    internal VoiceTransferLimiter(VoiceNetworkCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public async ValueTask ThrottleAsync(int bytes, CancellationToken token)
    {
        if (bytes <= 0) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var snapshot = _coordinator.Snapshot;
            if (!snapshot.IsJoined || !snapshot.IsTrafficProtectionEnabled || snapshot.ConnectedPeers == 0)
            {
                _nextWriteAt = _clock.Elapsed;
                return;
            }

            var now = _clock.Elapsed;
            if (now - _lastAdjustment >= AdjustmentInterval)
            {
                _rate = snapshot.IsDegraded
                    ? Math.Max(MinimumBytesPerSecond, _rate / 2d)
                    : Math.Min(MaximumBytesPerSecond, _rate * 1.25d);
                _lastAdjustment = now;
            }

            if (_nextWriteAt < now) _nextWriteAt = now;
            _nextWriteAt += TimeSpan.FromSeconds(bytes / _rate);
            var delay = _nextWriteAt - _clock.Elapsed;
            while (delay > TimeSpan.Zero)
            {
                var current = _coordinator.Snapshot;
                if (!current.IsJoined || !current.IsTrafficProtectionEnabled || current.ConnectedPeers == 0)
                {
                    _nextWriteAt = _clock.Elapsed;
                    return;
                }

                await Task.Delay(
                    delay > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : delay,
                    token).ConfigureAwait(false);
                delay = _nextWriteAt - _clock.Elapsed;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
