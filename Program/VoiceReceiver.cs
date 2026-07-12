using System.Collections.Concurrent;
using System.Linq;

namespace Minecraft;

public sealed class VoiceReceiver
{
    private const int MaxBufferedFrames = 5;

    private sealed class JitterBuffer
    {
        public int ExpectedSequence;
        public readonly SortedDictionary<int, short[]> Frames = new();
        public int StartDelayTicks = 2;
        public DateTimeOffset LastReceivedUtc = DateTimeOffset.UtcNow;
        public bool IsSpeaking;
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
                buffer = new JitterBuffer { ExpectedSequence = sequence, StartDelayTicks = 2 };
                _buffers[peerId] = buffer;
            }

            if (sequence < buffer.ExpectedSequence) return;
            if (!buffer.Frames.ContainsKey(sequence))
            {
                buffer.Frames[sequence] = samples;
            }

            buffer.LastReceivedUtc = DateTimeOffset.UtcNow;
            if (samples.Length > 0) buffer.IsSpeaking = true;
            if (sequence >= buffer.ExpectedSequence + 200)
            {
                buffer.Frames.Clear();
                buffer.Frames[sequence] = samples;
                buffer.ExpectedSequence = sequence;
            }
            while (buffer.Frames.Count > MaxBufferedFrames)
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
                    }
                    else
                    {
                        output = Array.Empty<short>();
                    }
                }

                mixed[peerId] = output;
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

    private short[] PadFrame(short[] samples)
    {
        if (samples.Length >= _frameSamples) return samples;
        var padded = new short[_frameSamples];
        Buffer.BlockCopy(samples, 0, padded, 0, samples.Length * sizeof(short));
        return padded;
    }
}
