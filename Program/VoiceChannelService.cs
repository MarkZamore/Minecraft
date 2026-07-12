using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;

namespace Minecraft;

public sealed class VoiceChannelService : IDisposable, IAsyncDisposable
{
    private const int ProtocolVersion = 2;
    private const int SpeakingThreshold = 300;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpeakingHold = TimeSpan.FromMilliseconds(250);
    private readonly Logger _logger;
    private readonly VoiceRuntimeOptions _runtimeOptions;
    private readonly VoiceDeviceManager _deviceManager = new();
    private readonly VoiceTransport _transport;
    private readonly VoiceEncoder _encoder = new();
    private readonly VoiceReceiver _receiver;
    private readonly Dictionary<string, VoicePeerRoute> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VoiceDecoder> _decoders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _peerVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly object _captureLock = new();
    private readonly object _encoderLock = new();

    private VoiceCapture? _capture;
    private VoicePlayback? _playback;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private Timer? _speakingTimer;
    private string _selfPeerId = "";
    private string _inputDeviceId = "";
    private string _outputDeviceId = "";
    private int _nextSequence;
    private bool _isJoined;
    private bool _isMuted;
    private bool _isDeafened;
    private bool _isPttActive;
    private bool _disposed;
    private bool _captureStarted;
    private bool _localSpeaking;
    private DateTimeOffset _localSpeakingUntil;
    private double _inputVolume = 1d;
    private double _outputVolume = 1d;
    private string _lastPacketError = "";
    private DateTimeOffset _lastPacketWarningAt = DateTimeOffset.MinValue;

    public VoiceChannelService(Logger logger, VoiceRuntimeOptions? runtimeOptions = null)
    {
        _logger = logger;
        _runtimeOptions = runtimeOptions ?? new VoiceRuntimeOptions();
        _runtimeOptions.Validate();
        _transport = new VoiceTransport(logger);
        _receiver = new VoiceReceiver(VoiceEncoder.FrameSamples);
    }

    public bool IsJoined => _isJoined;
    public bool IsMuted => _isMuted;
    public bool IsDeafened => _isDeafened;
    public string SelfPeerId => _selfPeerId;
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
            lock (_stateLock) return _routes.Keys.ToArray();
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

    public void SetMasterVolume(double volume) => SetOutputVolume(volume);

    public void SetInputVolume(double volume)
    {
        _inputVolume = Math.Clamp(volume, 0d, 2d);
    }

    public void SetOutputVolume(double volume)
    {
        _outputVolume = Math.Clamp(volume, 0d, 2d);
        lock (_stateLock) _playback?.SetMasterVolume(_outputVolume);
    }

    public void SetMuted(bool muted)
    {
        if (_isMuted == muted) return;
        _isMuted = muted;
        if (_isJoined && !_isMuted && _isPttActive) StartCapture();
        else StopCapture();
    }

    public void SetDeafened(bool deafened)
    {
        _isDeafened = deafened;
        lock (_stateLock) _playback?.SetDeafened(deafened);
    }

    public void SetPttPressed(bool pressed)
    {
        if (!_isJoined || _disposed) return;
        _isPttActive = pressed;
        if (pressed && !_isMuted) StartCapture();
        else StopCapture();
    }

    public void UpdatePeers(IEnumerable<(string peerId, string ip)> peers)
    {
        var incoming = peers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.peerId) &&
                           !string.Equals(peer.peerId, _selfPeerId, StringComparison.OrdinalIgnoreCase) &&
                           IPAddress.TryParse(peer.ip, out _))
            .GroupBy(peer => peer.peerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(peer => new IPEndPoint(IPAddress.Parse(peer.ip), _runtimeOptions.Port))
                    .Distinct()
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        lock (_stateLock)
        {
            foreach (var removedPeer in _routes.Keys.Except(incoming.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                _routes.Remove(removedPeer);
                _peerVolumes.Remove(removedPeer);
                if (_decoders.Remove(removedPeer, out var decoder)) decoder.Dispose();
                _receiver.RemovePeer(removedPeer);
            }

            foreach (var pair in incoming)
            {
                _routes.TryGetValue(pair.Key, out var previous);
                var active = previous?.ActiveEndpoint is not null && pair.Value.Contains(previous.ActiveEndpoint)
                    ? previous.ActiveEndpoint
                    : null;
                _routes[pair.Key] = new VoicePeerRoute(pair.Value, active);
                if (!_peerVolumes.ContainsKey(pair.Key)) _peerVolumes[pair.Key] = 1d;
                _playback?.SetPeerVolume(pair.Key, _peerVolumes[pair.Key]);
            }
        }
    }

    public void Join()
    {
        lock (_stateLock)
        {
            if (_disposed || _isJoined) return;

            var inputDevices = _deviceManager.GetInputDevices();
            var outputDevices = _deviceManager.GetOutputDevices();
            var input = _deviceManager.FindById(inputDevices, _inputDeviceId) ??
                        (inputDevices.Count > 0 ? inputDevices[0] : null) ??
                        throw new InvalidOperationException("No microphone available.");
            var output = _deviceManager.FindById(outputDevices, _outputDeviceId) ??
                         (outputDevices.Count > 0 ? outputDevices[0] : null) ??
                         throw new InvalidOperationException("No speaker available.");
            _inputDeviceId = input.Id;
            _outputDeviceId = output.Id;

            _receiver.Reset();
            _lastPacketError = "";
            _nextSequence = 0;
            _captureStarted = false;
            _isPttActive = false;
            _playback = new VoicePlayback(
                _receiver,
                _deviceManager.OpenOutputDevice(_outputDeviceId),
                _outputVolume);
            _playback.SpeakingStateChanged += OnSpeakingStateChanged;
            _playback.SetDeafened(_isDeafened);
            foreach (var pair in _peerVolumes) _playback.SetPeerVolume(pair.Key, pair.Value);
            _playback.Start();

            _transport.StartListening(_runtimeOptions.ListenAddress, _runtimeOptions.Port, OnVoicePacketAsync);
            _heartbeatCts = new CancellationTokenSource();
            _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);
            _speakingTimer = new Timer(CheckLocalSpeakingTimeout, null, 100, 100);
            _isJoined = true;
        }
        _logger.Info($"Voice channel joined on UDP {_runtimeOptions.Port}.");
    }

    public void Leave()
    {
        LeaveAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask LeaveAsync()
    {
        StopCapture();
        CancellationTokenSource? heartbeatCts;
        Task? heartbeatTask;
        VoicePlayback? playback;
        Timer? speakingTimer;
        VoiceDecoder[] decoders;
        lock (_stateLock)
        {
            if (!_isJoined) return;
            _isJoined = false;
            _isPttActive = false;
            heartbeatCts = _heartbeatCts;
            heartbeatTask = _heartbeatTask;
            _heartbeatCts = null;
            _heartbeatTask = null;
            speakingTimer = _speakingTimer;
            _speakingTimer = null;
            playback = _playback;
            _playback = null;
            decoders = _decoders.Values.ToArray();
            _decoders.Clear();
            _routes.Clear();
            _peerVolumes.Clear();
        }

        heartbeatCts?.Cancel();
        if (heartbeatTask is not null)
        {
            try { await heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        heartbeatCts?.Dispose();
        speakingTimer?.Dispose();
        if (playback is not null)
        {
            playback.SpeakingStateChanged -= OnSpeakingStateChanged;
            playback.Dispose();
        }
        foreach (var decoder in decoders) decoder.Dispose();
        await _transport.StopAsync().ConfigureAwait(false);
        _receiver.Reset();
        SetLocalSpeaking(false);
        _logger.Info("Voice channel left.");
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IPEndPoint[] targets;
            lock (_stateLock)
            {
                targets = _routes.Values.SelectMany(route => route.Candidates).Distinct().ToArray();
            }
            if (targets.Length > 0)
            {
                var packet = BuildPacket(VoicePacketType.Hello, _selfPeerId, 0, Array.Empty<byte>());
                await _transport.SendAsync(targets, packet, token).ConfigureAwait(false);
            }
            await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
        }
    }

    private void StartCapture()
    {
        lock (_captureLock)
        {
            if (_disposed || !_isJoined || _captureStarted) return;
            try
            {
                _capture = new VoiceCapture();
                _capture.FrameCaptured += OnFrameCaptured;
                _capture.Start(_deviceManager.OpenInputDevice(_inputDeviceId));
                _captureStarted = true;
                _logger.Info("Voice capture started.");
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
    }

    private void StopCapture()
    {
        lock (_captureLock)
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
                SetLocalSpeaking(false);
            }
        }
    }

    private void OnFrameCaptured(short[] frame)
    {
        if (!_isJoined || _isMuted || !_isPttActive || frame.Length == 0) return;

        var volume = _inputVolume;
        if (Math.Abs(volume - 1d) > 0.001d)
        {
            var scaled = new short[frame.Length];
            for (var index = 0; index < frame.Length; index++)
            {
                scaled[index] = (short)Math.Clamp(frame[index] * volume, short.MinValue, short.MaxValue);
            }
            frame = scaled;
        }

        if (frame.Any(sample => Math.Abs((int)sample) >= SpeakingThreshold))
        {
            _localSpeakingUntil = DateTimeOffset.UtcNow + SpeakingHold;
            SetLocalSpeaking(true);
        }

        IPEndPoint[] targets;
        lock (_stateLock)
        {
            targets = _routes.Values
                .Where(route => route.ActiveEndpoint is not null)
                .Select(route => route.ActiveEndpoint!)
                .Distinct()
                .ToArray();
        }
        if (targets.Length == 0) return;

        byte[] payload;
        lock (_encoderLock) payload = _encoder.Encode(frame);
        if (payload.Length == 0) return;
        var packet = BuildPacket(
            VoicePacketType.Audio,
            _selfPeerId,
            Interlocked.Increment(ref _nextSequence),
            payload);
        _ = _transport.SendAsync(targets, packet, CancellationToken.None);
    }

    private async Task OnVoicePacketAsync(IPEndPoint remote, byte[] buffer)
    {
        try
        {
            if (!_isJoined || !TryParsePacket(buffer, out var type, out var peerId, out var sequence, out var payload)) return;
            if (string.Equals(peerId, _selfPeerId, StringComparison.OrdinalIgnoreCase)) return;

            VoicePeerRoute? route;
            lock (_stateLock)
            {
                if (!_routes.TryGetValue(peerId, out route) ||
                    !route.Candidates.Any(candidate => candidate.Address.Equals(remote.Address)))
                {
                    return;
                }
                route.ActiveEndpoint = new IPEndPoint(remote.Address, remote.Port);
            }

            if (type == VoicePacketType.Hello)
            {
                var ack = BuildPacket(VoicePacketType.Ack, _selfPeerId, 0, Array.Empty<byte>());
                await _transport.SendAsync([remote], ack, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (type == VoicePacketType.Ack) return;
            if (type != VoicePacketType.Audio || payload.Length == 0) return;

            VoiceDecoder decoder;
            lock (_stateLock)
            {
                if (!_decoders.TryGetValue(peerId, out decoder!))
                {
                    decoder = new VoiceDecoder();
                    _decoders[peerId] = decoder;
                }
            }
            short[] decoded;
            lock (decoder) decoded = decoder.Decode(payload);
            if (decoded.Length == 0) return;
            _receiver.AddFrame(peerId, sequence, decoded);
        }
        catch (Exception ex)
        {
            _lastPacketError = ex.Message;
            WarnPacketError("Voice packet processing failed: " + ex.Message);
        }
    }

    private void CheckLocalSpeakingTimeout(object? state)
    {
        if (_localSpeaking && DateTimeOffset.UtcNow >= _localSpeakingUntil) SetLocalSpeaking(false);
    }

    private void SetLocalSpeaking(bool speaking)
    {
        if (_localSpeaking == speaking) return;
        _localSpeaking = speaking;
        if (!string.IsNullOrWhiteSpace(_selfPeerId)) SpeakingStateChanged?.Invoke(_selfPeerId, speaking);
    }

    private void OnSpeakingStateChanged(string peerId, bool isSpeaking)
    {
        SpeakingStateChanged?.Invoke(peerId, isSpeaking);
    }

    private void WarnPacketError(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPacketWarningAt < TimeSpan.FromSeconds(15)) return;
        _lastPacketWarningAt = now;
        _logger.Warn(message);
    }

    private static byte[] BuildPacket(VoicePacketType type, string peerId, int sequence, byte[] payload)
    {
        var peerBytes = Encoding.UTF8.GetBytes(peerId);
        if (peerBytes.Length > ushort.MaxValue) peerBytes = peerBytes.Take(ushort.MaxValue).ToArray();
        if (payload.Length > ushort.MaxValue) payload = payload.Take(ushort.MaxValue).ToArray();

        var packet = new byte[2 + sizeof(ushort) + peerBytes.Length + sizeof(int) + sizeof(ushort) + payload.Length];
        packet[0] = ProtocolVersion;
        packet[1] = (byte)type;
        var offset = 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)), (ushort)peerBytes.Length);
        offset += sizeof(ushort);
        Buffer.BlockCopy(peerBytes, 0, packet, offset, peerBytes.Length);
        offset += peerBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(offset, sizeof(int)), sequence);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)), (ushort)payload.Length);
        offset += sizeof(ushort);
        if (payload.Length > 0) Buffer.BlockCopy(payload, 0, packet, offset, payload.Length);
        return packet;
    }

    private static bool TryParsePacket(
        byte[] packet,
        out VoicePacketType type,
        out string peerId,
        out int sequence,
        out byte[] payload)
    {
        type = default;
        peerId = "";
        sequence = 0;
        payload = Array.Empty<byte>();
        if (packet.Length < 2 + sizeof(ushort) + sizeof(int) + sizeof(ushort) || packet[0] != ProtocolVersion) return false;
        type = (VoicePacketType)packet[1];
        if (type is not (VoicePacketType.Hello or VoicePacketType.Ack or VoicePacketType.Audio)) return false;

        var offset = 2;
        var peerLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        if (peerLength == 0 || offset + peerLength + sizeof(int) + sizeof(ushort) > packet.Length) return false;
        peerId = Encoding.UTF8.GetString(packet.AsSpan(offset, peerLength));
        offset += peerLength;
        sequence = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        if (offset + payloadLength != packet.Length) return false;
        payload = payloadLength == 0 ? Array.Empty<byte>() : packet.AsSpan(offset, payloadLength).ToArray();
        return true;
    }

    private static (string id, string name) ResolveIdentity(AppSettings settings)
    {
        var identity = settings.LocalIdentityId?.Trim();
        var name = settings.LocalIdentityName?.Trim();
        if (!Guid.TryParse(identity, out var identityUuid) || identityUuid == Guid.Empty)
        {
            throw new InvalidDataException("Voice identity does not match Minecraft\\Personal\\UUID.json.");
        }
        if (string.IsNullOrWhiteSpace(name)) name = identity;
        return (identityUuid.ToString("D"), name);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await LeaveAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _capture?.Dispose();
        _deviceManager.Dispose();
        _encoder.Dispose();
        _receiver.Reset();
        GC.SuppressFinalize(this);
    }

    private sealed class VoicePeerRoute
    {
        public VoicePeerRoute(IReadOnlyList<IPEndPoint> candidates, IPEndPoint? activeEndpoint)
        {
            Candidates = candidates;
            ActiveEndpoint = activeEndpoint;
        }

        public IReadOnlyList<IPEndPoint> Candidates { get; }
        public IPEndPoint? ActiveEndpoint { get; set; }
    }

    private enum VoicePacketType : byte
    {
        Hello = 1,
        Ack = 2,
        Audio = 3
    }
}
