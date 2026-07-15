using System.Diagnostics;

namespace Minecraft;

public sealed class VoiceNetworkCoordinator
{
    private const double InitialBulkBytesPerSecond = 512d * 1024d;
    private const double MinimumBulkBytesPerSecond = 128d * 1024d;
    private const double MaximumBulkBytesPerSecond = 64d * 1024d * 1024d;
    private const double ReservedVoiceBitsPerSecondPerPeer = 128_000d;
    private static readonly TimeSpan AdjustmentInterval = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly object _limiterGate = new();
    private readonly Stopwatch _transferClock = Stopwatch.StartNew();
    private bool _joined;
    private bool _trafficProtectionEnabled = true;
    private int _connectedPeers;
    private double _lossPercent;
    private double _jitterMs;
    private double _rttMs;
    private double _estimatedCapacityBytesPerSecond;
    private TimeSpan _lastAdjustment;
    private TimeSpan _nextWriteAt;
    private bool _limiterActive;
    private int _limiterResetRequested = 1;

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
            if (_joined == joined) return;
            _joined = joined;
            if (!joined)
            {
                _connectedPeers = 0;
                _lossPercent = 0;
                _jitterMs = 0;
                _rttMs = 0;
            }
        }
        Interlocked.Exchange(ref _limiterResetRequested, 1);
    }

    public void SetTrafficProtectionEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_trafficProtectionEnabled == enabled) return;
            _trafficProtectionEnabled = enabled;
        }
        Interlocked.Exchange(ref _limiterResetRequested, 1);
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

    internal async ValueTask ThrottleAsync(int bytes, CancellationToken token)
    {
        if (bytes <= 0) return;
        TimeSpan scheduledAt;
        lock (_limiterGate)
        {
            var now = _transferClock.Elapsed;
            if (Interlocked.Exchange(ref _limiterResetRequested, 0) != 0)
            {
                ResetLimiter(now);
            }

            var snapshot = Snapshot;
            if (!snapshot.IsJoined || !snapshot.IsTrafficProtectionEnabled || snapshot.ConnectedPeers == 0)
            {
                ResetLimiter(now);
                return;
            }

            var reserve = ReservedVoiceBytesPerSecond(snapshot.ConnectedPeers);
            if (!_limiterActive)
            {
                _estimatedCapacityBytesPerSecond = InitialBulkBytesPerSecond + reserve;
                _lastAdjustment = now;
                _nextWriteAt = now;
                _limiterActive = true;
            }
            else if (now - _lastAdjustment >= AdjustmentInterval)
            {
                _estimatedCapacityBytesPerSecond = snapshot.IsDegraded
                    ? Math.Max(MinimumBulkBytesPerSecond + reserve, _estimatedCapacityBytesPerSecond / 2d)
                    : Math.Min(MaximumBulkBytesPerSecond + reserve, _estimatedCapacityBytesPerSecond * 1.25d);
                _lastAdjustment = now;
            }

            var bulkRate = Math.Clamp(
                _estimatedCapacityBytesPerSecond - reserve,
                MinimumBulkBytesPerSecond,
                MaximumBulkBytesPerSecond);
            if (_nextWriteAt < now) _nextWriteAt = now;
            _nextWriteAt += TimeSpan.FromSeconds(bytes / bulkRate);
            scheduledAt = _nextWriteAt;
        }

        var delay = scheduledAt - _transferClock.Elapsed;
        while (delay > TimeSpan.Zero)
        {
            var current = Snapshot;
            if (!current.IsJoined || !current.IsTrafficProtectionEnabled || current.ConnectedPeers == 0)
            {
                lock (_limiterGate)
                {
                    ResetLimiter(_transferClock.Elapsed);
                }
                return;
            }

            await Task.Delay(
                delay > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : delay,
                token).ConfigureAwait(false);
            delay = scheduledAt - _transferClock.Elapsed;
        }
    }

    private static double ReservedVoiceBytesPerSecond(int connectedPeers) =>
        Math.Max(1, connectedPeers) * ReservedVoiceBitsPerSecondPerPeer / 8d;

    private void ResetLimiter(TimeSpan now)
    {
        _limiterActive = false;
        _estimatedCapacityBytesPerSecond = 0;
        _lastAdjustment = now;
        _nextWriteAt = now;
    }

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
    private readonly VoiceNetworkCoordinator _coordinator;

    internal VoiceTransferLimiter(VoiceNetworkCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public ValueTask ThrottleAsync(int bytes, CancellationToken token) =>
        _coordinator.ThrottleAsync(bytes, token);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
