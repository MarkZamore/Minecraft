using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Minecraft;

public sealed class VoiceChannelService : IDisposable
{
    private const int PacketVersion = 1;
    private readonly Logger _logger;
    private readonly VoiceDeviceManager _deviceManager = new();
    private readonly VoiceTransport _transport = new();
    private readonly VoiceEncoder _encoder = new();
    private readonly VoiceReceiver _receiver;
    private readonly Dictionary<string, IPEndPoint> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _peerVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly object _codecLock = new();

    private VoiceCapture? _capture;
    private VoicePlayback? _playback;
    private string _selfPeerId = "";
    private string _inputDeviceId = "";
    private string _outputDeviceId = "";
    private int _inputDeviceIndex;
    private int _outputDeviceIndex;
    private int _nextSequence;
    private bool _isJoined;
    private bool _isMuted;
    private bool _isDeafened;
    private bool _isPttActive;
    private double _inputVolume = 1d;
    private double _outputVolume = 1d;
    private bool _disposed;
    private bool _captureStarted;
    private string _lastPacketError = "";

    public VoiceChannelService(Logger logger)
    {
        _logger = logger;
        _receiver = new VoiceReceiver(VoiceEncoder.FrameSamples);
    }

    public bool IsJoined => _isJoined;
    public bool IsMuted => _isMuted;
    public bool IsDeafened => _isDeafened;
    public string LastPacketError => _lastPacketError;
    public event Action<string, bool>? SpeakingStateChanged;

    public IReadOnlyList<VoiceAudioDevice> GetInputDevices() => _deviceManager.GetInputDevices();
    public IReadOnlyList<VoiceAudioDevice> GetOutputDevices() => _deviceManager.GetOutputDevices();

    public void Initialize(AppSettings settings)
    {
        var identity = ResolveIdentity(settings);
        _selfPeerId = identity.id;
        _inputDeviceId = settings.VoiceInputDeviceId ?? "";
        _outputDeviceId = settings.VoiceOutputDeviceId ?? "";
        _isMuted = settings.VoiceMuted;
        _isDeafened = settings.VoiceDeafened;
        _inputVolume = Math.Clamp(settings.VoiceInputVolume, 0d, 2d);
        _outputVolume = Math.Clamp(settings.VoiceOutputVolume, 0d, 2d);
    }

    public void SetDeviceIds(string inputDeviceId, string outputDeviceId)
    {
        _inputDeviceId = inputDeviceId?.Trim() ?? "";
        _outputDeviceId = outputDeviceId?.Trim() ?? "";
    }

    public IReadOnlyCollection<string> KnownPeers
    {
        get
        {
            lock (_stateLock)
            {
                return _peers.Keys.ToArray();
            }
        }
    }

    public void SetPeerVolume(string peerId, double volume)
    {
        lock (_stateLock)
        {
            _peerVolumes[peerId] = Math.Clamp(volume, 0d, 2d);
            _playback?.SetPeerVolume(peerId, _peerVolumes[peerId]);
        }
    }

    public double GetPeerVolume(string peerId)
    {
        lock (_stateLock)
        {
            return _peerVolumes.TryGetValue(peerId, out var volume) ? volume : 1d;
        }
    }

    public void SetMasterVolume(double volume)
    {
        SetOutputVolume(volume);
    }

    public void SetInputVolume(double volume)
    {
        _inputVolume = Math.Clamp(volume, 0d, 2d);
    }

    public void SetOutputVolume(double volume)
    {
        _outputVolume = Math.Clamp(volume, 0d, 2d);
        _playback?.SetMasterVolume(_outputVolume);
    }

    public void SetMuted(bool muted)
    {
        if (_isMuted == muted) return;
        _isMuted = muted;
        if (_isJoined && !_isMuted && _isPttActive)
        {
            StartCaptureNoLock();
        }
        else
        {
            StopCaptureNoLock();
        }
    }

    public void SetDeafened(bool deafened)
    {
        _isDeafened = deafened;
        _playback?.SetDeafened(deafened);
    }

    public void SetPttPressed(bool pressed)
    {
        if (!_isJoined || _disposed)
        {
            return;
        }

        _isPttActive = pressed;
        if (pressed && !_isMuted)
        {
            StartCaptureNoLock();
        }
        else
        {
            StopCaptureNoLock();
        }
    }

    public void UpdatePeers(IEnumerable<(string peerId, string ip)> peers)
    {
        lock (_stateLock)
        {
            var preservedVolumes = new Dictionary<string, double>(_peerVolumes, StringComparer.OrdinalIgnoreCase);
            _peers.Clear();
            _peerVolumes.Clear();
            foreach (var (peerId, ip) in peers.Where(p => !string.IsNullOrWhiteSpace(p.peerId)))
            {
                if (string.Equals(peerId, _selfPeerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IPAddress.TryParse(ip, out var parsedIp))
                {
                    continue;
                }

                _peers[peerId] = new IPEndPoint(parsedIp, VoiceTransport.VoicePort);
                _peerVolumes[peerId] = preservedVolumes.TryGetValue(peerId, out var existingVolume)
                    ? existingVolume
                    : 1d;
            }
        }
    }

    public void Join()
    {
        lock (_stateLock)
        {
            if (_disposed || _isJoined) return;

            _receiver.Reset();
            StartTransportLocked();
            _lastPacketError = "";
            _nextSequence = 0;
            _captureStarted = false;
            _isPttActive = false;

            var inputDevices = _deviceManager.GetInputDevices();
            var outputDevices = _deviceManager.GetOutputDevices();
            var input = _deviceManager.FindById(inputDevices, _inputDeviceId) ??
                        (inputDevices.Count > 0 ? inputDevices[0] : null) ??
                        throw new InvalidOperationException("No microphone available.");
            var output = _deviceManager.FindById(outputDevices, _outputDeviceId) ??
                         (outputDevices.Count > 0 ? outputDevices[0] : null) ??
                         throw new InvalidOperationException("No speaker available.");
            _inputDeviceIndex = input.WaveInDeviceIndex;
            _outputDeviceIndex = output.WaveOutDeviceIndex;
            _inputDeviceId = input.Id;
            _outputDeviceId = output.Id;

            _playback = new VoicePlayback(_receiver, _outputDeviceIndex, _outputVolume);
            _playback.SpeakingStateChanged += OnSpeakingStateChanged;
            _playback.SetDeafened(_isDeafened);
            foreach (var pair in _peerVolumes)
            {
                _playback.SetPeerVolume(pair.Key, pair.Value);
            }

            _playback.Start();
            _isJoined = true;
        }
    }

    public void Leave()
    {
        StopCaptureNoLock();
        lock (_stateLock)
        {
            if (!_isJoined) return;

            _isJoined = false;
            _isPttActive = false;

            _playback?.Stop();
            if (_playback is not null)
            {
                _playback.SpeakingStateChanged -= OnSpeakingStateChanged;
                _playback.Dispose();
            }

            _playback = null;
            _capture?.Dispose();
            _capture = null;
            _transport.Stop();
            _peerVolumes.Clear();
            _peers.Clear();
            _receiver.Reset();
        }
    }

    private void StartTransportLocked()
    {
        _transport.StartListening(VoiceTransport.VoicePort, OnVoicePacketAsync);
    }

    private void StartCaptureNoLock()
    {
        if (_disposed || !_isJoined || _captureStarted)
        {
            return;
        }

        try
        {
            _capture = new VoiceCapture();
            _capture.FrameCaptured += OnFrameCaptured;
            _capture.Start(_inputDeviceIndex);
            _captureStarted = true;
        }
        catch (Exception ex)
        {
            _lastPacketError = ex.Message;
            _logger.Warn("Voice capture start failed: " + ex.Message);
            _capture?.Dispose();
            _capture = null;
            _captureStarted = false;
            throw;
        }
    }

    private void StopCaptureNoLock()
    {
        if (_capture is null) return;

        try
        {
            _capture.FrameCaptured -= OnFrameCaptured;
            _capture.Dispose();
        }
        finally
        {
            _capture = null;
            _captureStarted = false;
        }
    }

    private void OnFrameCaptured(short[] frame)
    {
        if (!_isJoined || _isMuted || !_isPttActive || frame.Length == 0)
        {
            return;
        }

        var volume = _inputVolume;
        if (Math.Abs(volume - 1d) > 0.001d)
        {
            var scaled = new short[frame.Length];
            for (var i = 0; i < frame.Length; i++)
            {
                scaled[i] = (short)Math.Clamp(frame[i] * volume, short.MinValue, short.MaxValue);
            }

            frame = scaled;
        }

        byte[] payload;
        lock (_codecLock)
        {
            payload = _encoder.Encode(frame);
        }
        if (payload.Length == 0)
        {
            return;
        }

        var targetPeers = _peers.Values.ToList();
        if (targetPeers.Count == 0)
        {
            return;
        }

        var packet = BuildPacket(_selfPeerId, Interlocked.Increment(ref _nextSequence), payload);
        _ = _transport.SendAsync(targetPeers, packet, CancellationToken.None);
    }

    private Task OnVoicePacketAsync(IPEndPoint remote, byte[] buffer)
    {
        try
        {
            if (!_isJoined || buffer.Length <= 0)
            {
                return Task.CompletedTask;
            }

            if (!TryParsePacket(buffer, out var peerId, out var sequence, out var payload))
            {
                return Task.CompletedTask;
            }

            if (string.Equals(peerId, _selfPeerId, StringComparison.OrdinalIgnoreCase))
            {
                return Task.CompletedTask;
            }

            short[] decoded;
            lock (_codecLock)
            {
                decoded = _encoder.Decode(payload);
            }
            if (decoded.Length == 0)
            {
                return Task.CompletedTask;
            }

            _receiver.AddFrame(peerId, sequence, decoded);
            _playback?.Start();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _lastPacketError = ex.Message;
            return Task.CompletedTask;
        }
    }

    private void OnSpeakingStateChanged(string peerId, bool isSpeaking)
    {
        SpeakingStateChanged?.Invoke(peerId, isSpeaking);
    }

    private static byte[] BuildPacket(string peerId, int sequence, byte[] payload)
    {
        var peerBytes = Encoding.UTF8.GetBytes(peerId);
        if (peerBytes.Length > ushort.MaxValue)
        {
            peerBytes = peerBytes.Take(ushort.MaxValue).ToArray();
        }

        if (payload.Length > ushort.MaxValue)
        {
            payload = payload.Take(ushort.MaxValue).ToArray();
        }

        var packet = new byte[1 + sizeof(ushort) + peerBytes.Length + sizeof(int) + sizeof(ushort) + payload.Length];
        packet[0] = PacketVersion;

        var offset = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)), (ushort)peerBytes.Length);
        offset += sizeof(ushort);
        if (peerBytes.Length > 0)
        {
            Buffer.BlockCopy(peerBytes, 0, packet, offset, peerBytes.Length);
            offset += peerBytes.Length;
        }

        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(offset, sizeof(int)), sequence);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)), (ushort)payload.Length);
        offset += sizeof(ushort);
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(payload, 0, packet, offset, payload.Length);
        }

        return packet;
    }

    private static bool TryParsePacket(byte[] packet, out string peerId, out int sequence, out byte[] payload)
    {
        peerId = "";
        sequence = 0;
        payload = Array.Empty<byte>();

        if (packet.Length < 1 + 2 + 4 + 2)
        {
            return false;
        }

        if (packet[0] != PacketVersion)
        {
            return false;
        }

        var offset = 1;
        var peerIdLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)));
        if (peerIdLength > packet.Length - offset - 2 - 4 - 2)
        {
            return false;
        }
        offset += sizeof(ushort);
        if (offset + peerIdLength > packet.Length)
        {
            return false;
        }

        var peerIdBytes = packet.AsSpan(offset, peerIdLength);
        peerId = Encoding.UTF8.GetString(peerIdBytes);
        offset += peerIdLength;

        if (offset + sizeof(int) > packet.Length)
        {
            return false;
        }
        sequence = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);

        if (offset + sizeof(ushort) > packet.Length)
        {
            return false;
        }
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        if (offset + payloadLength > packet.Length)
        {
            return false;
        }

        payload = payloadLength == 0 ? Array.Empty<byte>() : packet.AsSpan(offset, payloadLength).ToArray();
        return true;
    }

    private static (string id, string name) ResolveIdentity(AppSettings settings)
    {
        var identity = settings.LocalIdentityId?.Trim();
        var name = settings.LocalIdentityName?.Trim();
        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = string.IsNullOrWhiteSpace(name) ? "Player" : name;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = identity;
        }

        return (identity, name);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Leave();
        _transport.Dispose();
        _capture?.Dispose();
        _encoder.Dispose();
        _receiver.Reset();
    }
}
