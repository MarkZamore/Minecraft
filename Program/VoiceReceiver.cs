using System.Collections.Concurrent;
using System.Linq;

namespace Minecraft;

public sealed class VoiceReceiver
{
    private const int MinimumBufferedFrames = 3;
    private const int MaximumBufferedFrames = 10;

    private sealed class JitterBuffer
    {
        public int ExpectedSequence;
        public readonly SortedDictionary<int, short[]> Frames = new();
        public int StartDelayTicks = MinimumBufferedFrames;
        public int TargetFrames = MinimumBufferedFrames;
        public DateTimeOffset LastReceivedUtc = DateTimeOffset.UtcNow;
        public DateTimeOffset PreviousReceivedUtc;
        public bool IsSpeaking;
        public long ReceivedFrames;
        public long MissingFrames;
        public double JitterMs;
        public int StableFrames;
    }

    private readonly int _frameSamples;
    private readonly Dictionary<string, JitterBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public VoiceReceiver(int frameSamples)
    {
        _frameSamples = frameSamples;
    }

    public void AddFrame(string peerId, int sequence, short[] samples)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(peerId, out var buffer))
            {
                buffer = new JitterBuffer { ExpectedSequence = sequence };
                _buffers[peerId] = buffer;
            }

            if (sequence < buffer.ExpectedSequence) return;
            if (!buffer.Frames.ContainsKey(sequence))
            {
                buffer.Frames[sequence] = samples;
            }

            var now = DateTimeOffset.UtcNow;
            if (buffer.PreviousReceivedUtc != default)
            {
                var intervalError = Math.Abs((now - buffer.PreviousReceivedUtc).TotalMilliseconds - 20d);
                buffer.JitterMs += (intervalError - buffer.JitterMs) / 16d;
            }
            buffer.PreviousReceivedUtc = now;
            buffer.LastReceivedUtc = now;
            buffer.ReceivedFrames++;
            if (samples.Length > 0) buffer.IsSpeaking = true;
            if (sequence >= buffer.ExpectedSequence + 200)
            {
                buffer.Frames.Clear();
                buffer.Frames[sequence] = samples;
                buffer.ExpectedSequence = sequence;
            }
            while (buffer.Frames.Count > MaximumBufferedFrames + 4)
            {
                var oldest = buffer.Frames.Keys.First();
                buffer.Frames.Remove(oldest);
                if (oldest >= buffer.ExpectedSequence) buffer.ExpectedSequence = oldest + 1;
            }
        }
    }

    public Dictionary<string, short[]> DrainForPlayback()
    {
        var mixed = new Dictionary<string, short[]>();
        lock (_lock)
        {
            var stale = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
            var removeKeys = _buffers
                .Where(pair => pair.Value.Frames.Count == 0 && pair.Value.LastReceivedUtc < stale)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in removeKeys) _buffers.Remove(key);

            foreach (var pair in _buffers)
            {
                var peerId = pair.Key;
                var buffer = pair.Value;
                short[] output;
                if (buffer.StartDelayTicks > 0)
                {
                    buffer.StartDelayTicks--;
                    output = Array.Empty<short>();
                }
                else
                {
                    if (buffer.Frames.TryGetValue(buffer.ExpectedSequence, out var frame))
                    {
                        buffer.Frames.Remove(buffer.ExpectedSequence);
                        output = frame.Length == _frameSamples ? frame : PadFrame(frame);
                        buffer.ExpectedSequence++;
                    }
                    else if (buffer.Frames.Count > 0)
                    {
                        output = new short[_frameSamples];
                        buffer.ExpectedSequence++;
                        buffer.MissingFrames++;
                        buffer.StableFrames = 0;
                        buffer.TargetFrames = Math.Min(MaximumBufferedFrames, buffer.TargetFrames + 1);
                        buffer.StartDelayTicks = Math.Max(buffer.StartDelayTicks, buffer.TargetFrames - buffer.Frames.Count);
                    }
                    else
                    {
                        output = Array.Empty<short>();
                    }
                }

                mixed[peerId] = output;
                if (output.Length > 0 && output.Any(v => v != 0))
                {
                    buffer.StableFrames++;
                    if (buffer.StableFrames >= 250 && buffer.JitterMs < 20d && buffer.TargetFrames > MinimumBufferedFrames)
                    {
                        buffer.TargetFrames--;
                        buffer.StableFrames = 0;
                    }
                }
                if (!output.Any(v => v != 0))
                {
                    buffer.IsSpeaking = false;
                }
            }
        }

        return mixed;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _buffers.Clear();
        }
    }

    public void RemovePeer(string peerId)
    {
        lock (_lock)
        {
            _buffers.Remove(peerId);
        }
    }

    public bool IsPeerSpeaking(string peerId)
    {
        lock (_lock)
        {
            return _buffers.TryGetValue(peerId, out var buffer) && buffer.IsSpeaking;
        }
    }

    public VoiceReceiverQuality GetQuality()
    {
        lock (_lock)
        {
            if (_buffers.Count == 0) return new VoiceReceiverQuality(0, 0, MinimumBufferedFrames * 20);
            var received = _buffers.Values.Sum(buffer => buffer.ReceivedFrames);
            var missing = _buffers.Values.Sum(buffer => buffer.MissingFrames);
            var total = received + missing;
            var loss = total == 0 ? 0 : missing * 100d / total;
            return new VoiceReceiverQuality(
                loss,
                _buffers.Values.Max(buffer => buffer.JitterMs),
                _buffers.Values.Max(buffer => buffer.TargetFrames) * 20);
        }
    }

    private short[] PadFrame(short[] samples)
    {
        if (samples.Length >= _frameSamples) return samples;
        var padded = new short[_frameSamples];
        Buffer.BlockCopy(samples, 0, padded, 0, samples.Length * sizeof(short));
        return padded;
    }
}

public readonly record struct VoiceReceiverQuality(double LossPercent, double JitterMs, int BufferMilliseconds);
