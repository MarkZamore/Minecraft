using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Minecraft;

public sealed class VoiceCapture : IDisposable
{
    private const int SampleRate = 48000;
    private const int FrameSamples = 960;
    private readonly object _captureGate = new();
    private readonly List<short> _pendingSamples = new(FrameSamples * 2);
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private BufferedWaveProvider? _captureBuffer;
    private ISampleProvider? _sampleProvider;
    private float[] _readBuffer = Array.Empty<float>();

    public event Action<short[]>? FrameCaptured;
    public bool IsRunning { get; private set; }

    public void Start(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (IsRunning)
        {
            device.Dispose();
            return;
        }

        try
        {
            _device = device;
            _capture = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: 20);
            _captureBuffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(250),
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

            ISampleProvider samples = _captureBuffer.ToSampleProvider();
            if (samples.WaveFormat.Channels != 1)
            {
                samples = new DownmixToMonoSampleProvider(samples);
            }
            if (samples.WaveFormat.SampleRate != SampleRate)
            {
                samples = new WdlResamplingSampleProvider(samples, SampleRate);
            }

            _sampleProvider = samples;
            _readBuffer = new float[FrameSamples * 4];
            _pendingSamples.Clear();
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            IsRunning = true;
        }
        catch
        {
            DisposeCapture();
            throw;
        }
    }

    public void Stop()
    {
        var capture = _capture;
        if (capture is null)
        {
            IsRunning = false;
            return;
        }

        try
        {
            capture.StopRecording();
        }
        finally
        {
            lock (_captureGate)
            {
                if (ReferenceEquals(capture, _capture))
                {
                    DisposeCapture();
                }
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_captureGate)
        {
            if (_captureBuffer is null || _sampleProvider is null || e.BytesRecorded <= 0)
            {
                return;
            }

            _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            while (true)
            {
                var samplesRead = _sampleProvider.Read(_readBuffer, 0, _readBuffer.Length);
                if (samplesRead <= 0)
                {
                    break;
                }

                for (var index = 0; index < samplesRead; index++)
                {
                    var sample = Math.Clamp(_readBuffer[index], -1f, 1f);
                    _pendingSamples.Add((short)Math.Round(sample * short.MaxValue));
                }

                EmitCompleteFrames();
                if (samplesRead < _readBuffer.Length)
                {
                    break;
                }
            }
        }
    }

    private void EmitCompleteFrames()
    {
        while (_pendingSamples.Count >= FrameSamples)
        {
            var frame = _pendingSamples.GetRange(0, FrameSamples).ToArray();
            _pendingSamples.RemoveRange(0, FrameSamples);
            FrameCaptured?.Invoke(frame);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        IsRunning = false;
    }

    private void DisposeCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
        _captureBuffer = null;
        _sampleProvider = null;
        _readBuffer = Array.Empty<float>();
        _pendingSamples.Clear();
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = Array.Empty<float>();

        public DownmixToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            if (_channels < 2)
            {
                throw new ArgumentException("Downmixing requires at least two channels.", nameof(source));
            }

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requiredSamples = checked(count * _channels);
            if (_sourceBuffer.Length < requiredSamples)
            {
                _sourceBuffer = new float[requiredSamples];
            }

            var sourceSamples = _source.Read(_sourceBuffer, 0, requiredSamples);
            var frames = sourceSamples / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                var sourceOffset = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _sourceBuffer[sourceOffset + channel];
                }
                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}
