using System.Timers;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Timer = System.Timers.Timer;

namespace Minecraft;

public sealed class VoicePlayback : IDisposable
{
    private const int SpeakingThreshold = 300;
    private readonly BufferedWaveProvider _provider;
    private readonly WasapiOut _waveOut;
    private readonly MMDevice _device;
    private readonly VoiceReceiver _receiver;
    private readonly Timer _playbackTimer;
    private readonly Dictionary<string, double> _peerVolumes;
    private readonly object _stateGate = new();
    private double _masterVolume;
    private bool _deafened;
    private bool _isDisposed;
    private bool _isStarted;

    public event Action<string, bool>? SpeakingStateChanged;

    public VoicePlayback(VoiceReceiver receiver, MMDevice outputDevice, double masterVolume)
    {
        _receiver = receiver;
        _device = outputDevice ?? throw new ArgumentNullException(nameof(outputDevice));
        _peerVolumes = new(StringComparer.OrdinalIgnoreCase);
        _provider = new BufferedWaveProvider(new WaveFormat(VoiceEncoder.SampleRate, 16, VoiceEncoder.Channels))
        {
            DiscardOnBufferOverflow = true,
            BufferLength = VoiceEncoder.SampleRate * 4
        };
        var waveOut = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        try
        {
            waveOut.Init(_provider);
        }
        catch
        {
            waveOut.Dispose();
            _device.Dispose();
            throw;
        }
        _waveOut = waveOut;
        _masterVolume = Math.Clamp(masterVolume, 0d, 2d);

        _playbackTimer = new Timer(20)
        {
            AutoReset = true
        };
        _playbackTimer.Elapsed += (_, _) => Render();
    }

    public void Start()
    {
        lock (_stateGate)
        {
            if (_isDisposed || _isStarted) return;
            _isStarted = true;
            _waveOut.Play();
            _playbackTimer.Start();
        }
    }

    public void Stop()
    {
        lock (_stateGate)
        {
            _isStarted = false;
            _playbackTimer.Stop();
            _waveOut.Stop();
            _provider.ClearBuffer();
        }
    }

    public void SetPeerVolume(string peerId, double volume)
    {
        lock (_stateGate)
        {
            _peerVolumes[peerId] = Math.Clamp(volume, 0d, 2d);
        }
    }

    public void SetMasterVolume(double volume)
    {
        lock (_stateGate)
        {
            _masterVolume = Math.Clamp(volume, 0d, 2d);
        }
    }

    public void SetDeafened(bool deafened)
    {
        lock (_stateGate)
        {
            _deafened = deafened;
        }
    }

    private void Render()
    {
        Dictionary<string, double> peerVolumes;
        double masterVolume;
        bool deafened;
        lock (_stateGate)
        {
            if (_isDisposed || !_isStarted) return;
            deafened = _deafened;
            masterVolume = _masterVolume;
            peerVolumes = new Dictionary<string, double>(_peerVolumes, StringComparer.OrdinalIgnoreCase);
        }
        if (deafened)
        {
            _provider.ClearBuffer();
            return;
        }

        var frames = _receiver.DrainForPlayback();
        if (frames.Count == 0)
        {
            return;
        }

        var mixed = new short[VoiceEncoder.FrameSamples];
        var speakers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in frames)
        {
            var peerId = pair.Key;
            var frame = pair.Value;
            var volume = peerVolumes.TryGetValue(peerId, out var value) ? value : 1d;
            var isSpeaking = frame.Any(sample => Math.Abs((int)sample) >= SpeakingThreshold);
            speakers[peerId] = isSpeaking;

            var scale = (float)(volume * masterVolume);
            for (var index = 0; index < VoiceEncoder.FrameSamples && index < frame.Length; index++)
            {
                var mixedValue = (float)mixed[index] + (frame[index] * scale);
                mixed[index] = (short)Math.Clamp(mixedValue, short.MinValue, short.MaxValue);
            }
        }

        foreach (var pair in speakers)
        {
            SpeakingStateChanged?.Invoke(pair.Key, pair.Value);
        }

        var bytes = new byte[mixed.Length * sizeof(short)];
        Buffer.BlockCopy(mixed, 0, bytes, 0, bytes.Length);
        _provider.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isStarted = false;
            _playbackTimer.Stop();
            _playbackTimer.Dispose();
            _waveOut.Stop();
            _waveOut.Dispose();
            _device.Dispose();
        }
    }
}
