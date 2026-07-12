using System.Timers;
using NAudio.Wave;
using System;
using System.Linq;
using System.Collections.Generic;
using Timer = System.Timers.Timer;

namespace Minecraft;

public sealed class VoicePlayback : IDisposable
{
    private readonly BufferedWaveProvider _provider;
    private readonly WaveOutEvent _waveOut;
    private readonly VoiceReceiver _receiver;
    private readonly Timer _playbackTimer;
    private readonly Dictionary<string, double> _peerVolumes;
    private double _masterVolume;
    private bool _deafened;
    private bool _isDisposed;

    public event Action<string, bool>? SpeakingStateChanged;

    public VoicePlayback(VoiceReceiver receiver, int outputDeviceNumber, double masterVolume)
    {
        _receiver = receiver;
        _peerVolumes = new(StringComparer.OrdinalIgnoreCase);
        _provider = new BufferedWaveProvider(new WaveFormat(VoiceEncoder.SampleRate, 16, VoiceEncoder.Channels))
        {
            DiscardOnBufferOverflow = true,
            BufferLength = VoiceEncoder.SampleRate * 4
        };
        _waveOut = new WaveOutEvent
        {
            DeviceNumber = outputDeviceNumber
        };
        _waveOut.Init(_provider);
        _waveOut.Volume = 1f;
        _masterVolume = Math.Clamp(masterVolume, 0d, 2d);
        _waveOut.Play();

        _playbackTimer = new Timer(20)
        {
            AutoReset = true
        };
        _playbackTimer.Elapsed += (_, _) => Render();
    }

    public void Start()
    {
        if (_isDisposed) return;
        _receiver.Reset();
        _playbackTimer.Start();
    }

    public void Stop()
    {
        _playbackTimer.Stop();
        _provider.ClearBuffer();
    }

    public void SetPeerVolume(string peerId, double volume)
    {
        _peerVolumes[peerId] = Math.Clamp(volume, 0d, 2d);
    }

    public void SetMasterVolume(double volume)
    {
        _masterVolume = Math.Clamp(volume, 0d, 2d);
    }

    public void SetDeafened(bool deafened)
    {
        _deafened = deafened;
    }

    private void Render()
    {
        if (_isDisposed) return;
        if (_deafened)
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
            var volume = _peerVolumes.TryGetValue(peerId, out var value) ? value : 1d;
            var isSpeaking = frame.Any(sample => sample != 0);
            speakers[peerId] = isSpeaking;

            var scale = (float)(volume * _masterVolume);
            for (var i = 0; i < VoiceEncoder.FrameSamples && i < frame.Length; i++)
            {
                var mixedValue = (float)mixed[i] + (frame[i] * scale);
                mixed[i] = (short)Math.Clamp(mixedValue, short.MinValue, short.MaxValue);
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
        if (_isDisposed) return;
        _isDisposed = true;
        _playbackTimer.Stop();
        _playbackTimer.Dispose();
        _waveOut.Stop();
        _waveOut.Dispose();
    }
}
