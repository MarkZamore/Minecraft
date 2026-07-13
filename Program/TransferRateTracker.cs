using System.Diagnostics;

namespace Minecraft;

internal sealed class TransferRateTracker
{
    private string _scope = "";
    private long _lastBytes;
    private long _lastTimestamp;
    private double _smoothedBytesPerSecond;

    public double Update(long currentBytes, string scope)
    {
        var now = Stopwatch.GetTimestamp();
        currentBytes = Math.Max(0, currentBytes);
        if (!string.Equals(_scope, scope, StringComparison.Ordinal) ||
            _lastTimestamp == 0 ||
            currentBytes < _lastBytes)
        {
            _scope = scope;
            _lastBytes = currentBytes;
            _lastTimestamp = now;
            _smoothedBytesPerSecond = 0;
            return 0;
        }

        var elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        if (elapsed < 0.05d) return _smoothedBytesPerSecond;
        var instantaneous = Math.Max(0, currentBytes - _lastBytes) / elapsed;
        _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0
            ? instantaneous
            : (_smoothedBytesPerSecond * 0.7d) + (instantaneous * 0.3d);
        _lastBytes = currentBytes;
        _lastTimestamp = now;
        return _smoothedBytesPerSecond;
    }

    public void Reset()
    {
        _scope = "";
        _lastBytes = 0;
        _lastTimestamp = 0;
        _smoothedBytesPerSecond = 0;
    }
}
