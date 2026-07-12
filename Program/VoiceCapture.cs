using System.Collections.Generic;
using System.Collections.Concurrent;
using NAudio.Wave;

namespace Minecraft;

public sealed class VoiceCapture : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSamples = 960;
    private const int BitsPerSample = 16;
    private WaveInEvent? _waveIn;
    private readonly int _frameBytes = FrameSamples * (BitsPerSample / 8);
    private readonly List<byte> _leftover = new();

    public event Action<short[]>? FrameCaptured;
    public bool IsRunning { get; private set; }

    public void Start(int deviceIndex)
    {
        if (IsRunning)
        {
            return;
        }

        if (deviceIndex < 0)
        {
            deviceIndex = 0;
        }

        if (WaveIn.DeviceCount == 0)
        {
            throw new InvalidOperationException("No microphone input device found.");
        }

        if (deviceIndex >= WaveIn.DeviceCount)
        {
            deviceIndex = Math.Max(0, WaveIn.DeviceCount - 1);
        }

        _waveIn = new WaveInEvent
        {
            DeviceNumber = Math.Max(0, deviceIndex),
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 20,
            NumberOfBuffers = 4
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += (_, _) => IsRunning = false;
        _waveIn.StartRecording();
        IsRunning = true;
    }

    public void Stop()
    {
        if (_waveIn is null) return;

        try
        {
            _waveIn.StopRecording();
        }
        finally
        {
            _waveIn.Dispose();
            _waveIn = null;
            IsRunning = false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_waveIn is null) return;

        var leftoverCount = _leftover.Count;
        var all = new byte[e.BytesRecorded + leftoverCount];
        if (_leftover.Count > 0)
        {
            var prefix = _leftover.ToArray();
            Buffer.BlockCopy(prefix, 0, all, 0, leftoverCount);
            _leftover.Clear();
        }
        Buffer.BlockCopy(e.Buffer, 0, all, leftoverCount, e.BytesRecorded);

        var index = 0;
        while (index + _frameBytes <= all.Length)
        {
            var frame = new short[FrameSamples];
            var offset = 0;
            for (var sampleIndex = 0; sampleIndex < FrameSamples; sampleIndex++)
            {
                frame[sampleIndex] = BitConverter.ToInt16(all, index + offset);
                offset += sizeof(short);
            }
            index += _frameBytes;
            FrameCaptured?.Invoke(frame);
        }

        if (index < all.Length)
        {
            var remain = all.Length - index;
            _leftover.Clear();
            for (var i = 0; i < remain; i++)
            {
                _leftover.Add(all[index + i]);
            }
        }

    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
