using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Minecraft;

public sealed class VoiceChannelService : IDisposable, IAsyncDisposable
{
    private const int ProtocolVersion = 3;
    private const byte ProtectionPayloadVersion = 1;
    private const int ProtectionPayloadLength = 1 + 1 + sizeof(long) + 16;
    private const int SpeakingThreshold = 300;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SpeakingHold = TimeSpan.FromMilliseconds(250);
    private readonly Logger _logger;
    private readonly VoiceNetworkCoordinator _networkCoordinator;
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
    private int _nextAudioSequence;
    private int _nextHeartbeatSequence;
    private bool _isJoined;
    private bool _isMuted;
    private bool _isDeafened;
    private bool _isPttActive;
    private bool _disposed;
    private bool _captureStarted;
    private bool _localSpeaking;
    private bool _trafficProtectionEnabled = true;
    private long _trafficProtectionRevision;
    private long _maxTrafficProtectionRevision;
    private string _trafficProtectionOriginId = Guid.Empty.ToString("D");
    private DateTimeOffset _localSpeakingUntil;
    private double _inputVolume = 1d;
    private double _outputVolume = 1d;
    private string _lastPacketError = "";
    private DateTimeOffset _lastPacketWarningAt = DateTimeOffset.MinValue;

    public VoiceChannelService(
        Logger logger,
        VoiceNetworkCoordinator? networkCoordinator = null,
        VoiceRuntimeOptions? runtimeOptions = null,
        VirtualNetworkService? network = null)
    {
        _logger = logger;
        _networkCoordinator = networkCoordinator ?? new VoiceNetworkCoordinator();
        _runtimeOptions = runtimeOptions ?? new VoiceRuntimeOptions();
        _runtimeOptions.Validate();
        _transport = new VoiceTransport(logger, network ?? new VirtualNetworkService(logger));
        _receiver = new VoiceReceiver(VoiceEncoder.FrameSamples);
    }

    public bool IsJoined => _isJoined;
    public bool IsMuted => _isMuted;
    public bool IsDeafened => _isDeafened;
    public bool IsTrafficProtectionEnabled
    {
        get
        {
            lock (_stateLock) return _trafficProtectionEnabled;
        }
    }
    public string SelfPeerId => _selfPeerId;
    public string LastPacketError => _lastPacketError;
    public event Action<string, bool>? SpeakingStateChanged;
    public event Action<string, bool, bool>? PeerPresenceChanged;
    public event Action<bool>? TrafficProtectionChanged;

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

    public bool ToggleTrafficProtection()
    {
        bool enabled;
        long revision;
        var origin = NormalizeOriginId(_selfPeerId);
        lock (_stateLock)
        {
            if (!_isJoined || _disposed) return _trafficProtectionEnabled;
            enabled = !_trafficProtectionEnabled;
            revision = Math.Max(_maxTrafficProtectionRevision, _trafficProtectionRevision) + 1;
            _trafficProtectionEnabled = enabled;
            _trafficProtectionRevision = revision;
            _maxTrafficProtectionRevision = revision;
            _trafficProtectionOriginId = origin;
        }

        _networkCoordinator.SetTrafficProtectionEnabled(enabled);
        TrafficProtectionChanged?.Invoke(enabled);
        _ = BroadcastProtectionStateAsync();
        return enabled;
    }

    public void UpdatePeers(IEnumerable<(string peerId, string ip)> peers)
        => UpdatePeers(peers.Select(peer => new VoicePeerCandidate(peer.peerId, peer.ip, "")));

    public void UpdatePeers(IEnumerable<VoicePeerCandidate> peers)
    {
        var incoming = peers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.PeerId) &&
                           !string.Equals(peer.PeerId, _selfPeerId, StringComparison.OrdinalIgnoreCase) &&
                           IPAddress.TryParse(peer.Address, out _))
            .GroupBy(peer => peer.PeerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(peer => new VoiceRouteTarget(
                        new IPEndPoint(IPAddress.Parse(peer.Address), _runtimeOptions.Port),
                        peer.ProviderId?.Trim() ?? ""))
                    .Distinct()
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var removedPeer in _routes.Keys.Except(incoming.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                var route = _routes[removedPeer];
                if (now - route.LastDiscoverySeenUtc < TimeSpan.FromSeconds(30)) continue;
                RemovePeerLocked(removedPeer, explicitLeave: false);
            }

            foreach (var pair in incoming)
            {
                _routes.TryGetValue(pair.Key, out var previous);
                var active = previous?.ActiveTarget is not null && pair.Value.Contains(previous.ActiveTarget)
                    ? previous.ActiveTarget
                    : null;
                var route = previous ?? new VoicePeerRoute(pair.Value, active);
                route.Candidates = pair.Value;
                route.ActiveTarget = active;
                route.LastDiscoverySeenUtc = now;
                _routes[pair.Key] = route;
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
            _nextAudioSequence = 0;
            _nextHeartbeatSequence = 0;
            _captureStarted = false;
            _isPttActive = false;
            _trafficProtectionEnabled = true;
            _trafficProtectionRevision = 0;
            _maxTrafficProtectionRevision = 0;
            _trafficProtectionOriginId = NormalizeOriginId(_selfPeerId);
            _playback = new VoicePlayback(
                _receiver,
                _deviceManager.OpenOutputDevice(_outputDeviceId),
                _outputVolume);
            _playback.SpeakingStateChanged += OnSpeakingStateChanged;
            _playback.SetDeafened(_isDeafened);
            foreach (var pair in _peerVolumes) _playback.SetPeerVolume(pair.Key, pair.Value);
            _playback.Start();

            _transport.StartListening(
                _runtimeOptions.ListenAddress,
                _runtimeOptions.Port,
                OnVoicePacketAsync);
            _isJoined = true;
            _networkCoordinator.SetJoined(true);
            _networkCoordinator.SetTrafficProtectionEnabled(true);
            _heartbeatCts = new CancellationTokenSource();
            _heartbeatTask = HeartbeatLoopAsync(_heartbeatCts.Token);
            _speakingTimer = new Timer(CheckLocalSpeakingTimeout, null, 100, 100);
        }
        TrafficProtectionChanged?.Invoke(true);
        _logger.Info($"Voice channel joined on UDP {_runtimeOptions.Port}.");
    }

    public void Leave()
    {
        LeaveAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask LeaveAsync()
    {
        StopCapture();
        VoiceRouteTarget[] goodbyeTargets;
        lock (_stateLock)
        {
            goodbyeTargets = _routes.Values
                .SelectMany(route => route.ActiveTarget is { } active ? [active] : route.Candidates)
                .Distinct()
                .ToArray();
        }
        if (_isJoined && goodbyeTargets.Length > 0)
        {
            try
            {
                var goodbye = BuildPacket(VoicePacketType.Goodbye, _selfPeerId, 0, Array.Empty<byte>());
                await _transport.SendAsync(goodbyeTargets, goodbye, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WarnPacketError("Voice goodbye failed: " + ex.Message);
            }
        }
        CancellationTokenSource? heartbeatCts;
        Task? heartbeatTask;
        VoicePlayback? playback;
        Timer? speakingTimer;
        VoiceDecoder[] decoders;
        lock (_stateLock)
        {
            if (!_isJoined) return;
            _isJoined = false;
            _networkCoordinator.SetJoined(false);
            _networkCoordinator.SetTrafficProtectionEnabled(true);
            _isPttActive = false;
            _trafficProtectionEnabled = true;
            _trafficProtectionRevision = 0;
            _maxTrafficProtectionRevision = 0;
            _trafficProtectionOriginId = NormalizeOriginId(_selfPeerId);
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
        TrafficProtectionChanged?.Invoke(true);
        _logger.Info("Voice channel left.");
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            List<(string PeerId, VoiceRouteTarget[] Targets, int Nonce)> sends;
            var now = DateTimeOffset.UtcNow;
            lock (_stateLock)
            {
                sends = _routes.Select(pair =>
                {
                    if (pair.Value.ActiveTarget is not null &&
                        now - pair.Value.LastPacketUtc >= TimeSpan.FromSeconds(5))
                    {
                        pair.Value.ActiveTarget = null;
                    }
                    var nonce = Interlocked.Increment(ref _nextHeartbeatSequence);
                    pair.Value.PendingHeartbeatSequence = nonce;
                    pair.Value.PendingHeartbeatSentUtc = now;
                    return (pair.Key, pair.Value.Candidates.Distinct().ToArray(), nonce);
                }).ToList();
                foreach (var pair in _routes)
                {
                    if (pair.Value.PresenceAnnounced && now - pair.Value.LastPacketUtc >= TimeSpan.FromSeconds(30))
                    {
                        pair.Value.PresenceAnnounced = false;
                        PeerPresenceChanged?.Invoke(pair.Key, false, false);
                    }
                }
            }
            foreach (var send in sends)
            {
                var packet = BuildPacket(
                    VoicePacketType.Hello,
                    _selfPeerId,
                    send.Nonce,
                    BuildProtectionPayload());
                await _transport.SendAsync(send.Targets, packet, token).ConfigureAwait(false);
            }
            UpdateNetworkQuality();
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

        VoiceRouteTarget[] targets;
        lock (_stateLock)
        {
            targets = _routes.Values
                .Where(route => route.ActiveTarget is not null)
                .Select(route => route.ActiveTarget!)
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
            Interlocked.Increment(ref _nextAudioSequence),
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
            var notifyPresence = false;
            lock (_stateLock)
            {
                if (!_routes.TryGetValue(peerId, out route))
                {
                    return;
                }
                var matchedCandidate = FindMatchedCandidate(route, remote);
                if (matchedCandidate is null) return;
                var now = DateTimeOffset.UtcNow;
                route.ActiveTarget = matchedCandidate with
                {
                    EndPoint = new IPEndPoint(remote.Address, remote.Port)
                };
                route.LastPacketUtc = now;
                if (!route.PresenceAnnounced || now - route.LastPresenceNotificationUtc >= TimeSpan.FromSeconds(1))
                {
                    route.PresenceAnnounced = true;
                    route.LastPresenceNotificationUtc = now;
                    notifyPresence = true;
                }
            }
            if (notifyPresence) PeerPresenceChanged?.Invoke(peerId, true, false);

            if (type == VoicePacketType.Hello)
            {
                TryApplyProtectionPayload(payload);
                var ack = BuildPacket(
                    VoicePacketType.Ack,
                    _selfPeerId,
                    sequence,
                    BuildProtectionPayload());
                VoiceRouteTarget? replyTarget;
                lock (_stateLock)
                {
                    replyTarget = _routes.TryGetValue(peerId, out route)
                        ? route.ActiveTarget
                        : null;
                }
                if (replyTarget is not null)
                {
                    await _transport.SendAsync([replyTarget], ack, CancellationToken.None).ConfigureAwait(false);
                }
                return;
            }
            if (type == VoicePacketType.Ack)
            {
                TryApplyProtectionPayload(payload);
                lock (_stateLock)
                {
                    if (_routes.TryGetValue(peerId, out route) && route.PendingHeartbeatSequence == sequence)
                    {
                        route.RoundTripMs = Math.Max(0, (DateTimeOffset.UtcNow - route.PendingHeartbeatSentUtc).TotalMilliseconds);
                    }
                }
                UpdateNetworkQuality();
                return;
            }
            if (type == VoicePacketType.Goodbye)
            {
                lock (_stateLock)
                {
                    if (_routes.TryGetValue(peerId, out route))
                    {
                        route.PresenceAnnounced = false;
                        route.ActiveTarget = null;
                    }
                }
                PeerPresenceChanged?.Invoke(peerId, false, true);
                return;
            }
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
            if (!VoiceDecoder.IsTwentyMillisecondPacket(payload)) return;
            var recoveredFrames = new List<(int Sequence, short[] Samples)>();
            short[] decoded;
            lock (decoder)
            {
                int lastSequence;
                lock (_stateLock)
                {
                    if (!_routes.TryGetValue(peerId, out route)) return;
                    lastSequence = route.LastAudioSequence;
                    if (lastSequence > 0 && sequence <= lastSequence) return;
                }

                var missingCount = lastSequence <= 0 ? 0 : sequence - lastSequence - 1;
                if (missingCount > 6)
                {
                    decoder.Reset();
                    _receiver.RemovePeer(peerId);
                }
                else if (missingCount > 0)
                {
                    for (var missingSequence = lastSequence + 1;
                         missingSequence < sequence - 1;
                         missingSequence++)
                    {
                        var plc = decoder.DecodeMissing();
                        if (plc.Length > 0) recoveredFrames.Add((missingSequence, plc));
                    }
                    var fec = decoder.DecodeFec(payload);
                    if (fec.Length > 0) recoveredFrames.Add((sequence - 1, fec));
                }

                decoded = decoder.DecodePacket(payload);
                lock (_stateLock)
                {
                    if (_routes.TryGetValue(peerId, out route))
                    {
                        route.LastAudioSequence = sequence;
                    }
                }
            }
            foreach (var recovered in recoveredFrames)
            {
                _receiver.AddFrame(peerId, recovered.Sequence, recovered.Samples);
            }
            if (decoded.Length == 0) return;
            _receiver.AddFrame(peerId, sequence, decoded);
            UpdateNetworkQuality();
        }
        catch (Exception ex)
        {
            _lastPacketError = ex.Message;
            ResetPeerDecoderAfterFailure(buffer);
            WarnPacketError("Voice packet processing failed: " + ex.Message);
        }
    }

    private void ResetPeerDecoderAfterFailure(byte[] packet)
    {
        if (!TryParsePacket(packet, out _, out var peerId, out _, out _)) return;
        VoiceDecoder? decoder;
        lock (_stateLock)
        {
            _decoders.TryGetValue(peerId, out decoder);
            if (_routes.TryGetValue(peerId, out var route))
            {
                route.LastAudioSequence = 0;
            }
        }
        if (decoder is not null)
        {
            lock (decoder) decoder.Reset();
        }
        _receiver.RemovePeer(peerId);
    }

    private static VoiceRouteTarget? FindMatchedCandidate(VoicePeerRoute route, IPEndPoint remote)
    {
        var exact = route.Candidates.FirstOrDefault(candidate =>
            candidate.EndPoint.Address.Equals(remote.Address));
        if (exact is not null) return exact;

        var sameFamily = route.Candidates.FirstOrDefault(candidate =>
            candidate.EndPoint.AddressFamily == remote.AddressFamily);
        if (sameFamily is not null) return sameFamily;

        var normalized = NormalizeVoiceAddress(remote.Address);
        var mapped = normalized is not null
            ? route.Candidates.FirstOrDefault(candidate =>
                NormalizeVoiceAddress(candidate.EndPoint.Address) is { } candidateNormalized &&
                string.Equals(candidateNormalized, normalized, StringComparison.OrdinalIgnoreCase))
            : null;
        if (mapped is not null) return mapped;

        if (route.Candidates.Count == 1)
        {
            return route.Candidates[0];
        }

        return null;
    }

    private static string? NormalizeVoiceAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        if (!address.IsIPv4MappedToIPv6) return address.ToString();

        var ipv4 = address.MapToIPv4();
        return ipv4.ToString();
    }

    private void CheckLocalSpeakingTimeout(object? state)
    {
        if (_localSpeaking && DateTimeOffset.UtcNow >= _localSpeakingUntil) SetLocalSpeaking(false);
    }

    private void UpdateNetworkQuality()
    {
        var receiverQuality = _receiver.GetQuality();
        int connectedPeers;
        double averageRtt;
        lock (_stateLock)
        {
            var activeRoutes = _routes.Values
                .Where(route => route.PresenceAnnounced &&
                                DateTimeOffset.UtcNow - route.LastPacketUtc < TimeSpan.FromSeconds(5))
                .ToArray();
            connectedPeers = activeRoutes.Length;
            averageRtt = activeRoutes.Length == 0 ? 0 : activeRoutes.Average(route => route.RoundTripMs);
        }

        _networkCoordinator.ReportQuality(
            connectedPeers,
            receiverQuality.LossPercent,
            receiverQuality.JitterMs,
            averageRtt);
        var degraded = receiverQuality.LossPercent >= 4 || receiverQuality.JitterMs >= 60 || averageRtt >= 250;
        var moderate = receiverQuality.LossPercent >= 1 || receiverQuality.JitterMs >= 30 || averageRtt >= 120;
        var bitrate = degraded ? 32000 : moderate ? 48000 : 64000;
        lock (_encoderLock)
        {
            _encoder.ConfigureNetwork(bitrate, (int)Math.Ceiling(receiverQuality.LossPercent));
        }
    }

    private void RemovePeerLocked(string peerId, bool explicitLeave)
    {
        if (!_routes.Remove(peerId, out var route)) return;
        _peerVolumes.Remove(peerId);
        if (_decoders.Remove(peerId, out var decoder)) decoder.Dispose();
        _receiver.RemovePeer(peerId);
        if (route.PresenceAnnounced)
        {
            PeerPresenceChanged?.Invoke(peerId, false, explicitLeave);
        }
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

    private async Task BroadcastProtectionStateAsync()
    {
        try
        {
            List<(VoiceRouteTarget[] Targets, int Nonce)> sends;
            var now = DateTimeOffset.UtcNow;
            lock (_stateLock)
            {
                if (!_isJoined) return;
                sends = _routes.Values.Select(route =>
                {
                    var nonce = Interlocked.Increment(ref _nextHeartbeatSequence);
                    route.PendingHeartbeatSequence = nonce;
                    route.PendingHeartbeatSentUtc = now;
                    return (route.Candidates.Distinct().ToArray(), nonce);
                }).ToList();
            }

            var payload = BuildProtectionPayload();
            foreach (var send in sends)
            {
                var packet = BuildPacket(VoicePacketType.Hello, _selfPeerId, send.Nonce, payload);
                await _transport.SendAsync(send.Targets, packet, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            WarnPacketError("Voice protection sync failed: " + ex.Message);
        }
    }

    private byte[] BuildProtectionPayload()
    {
        bool enabled;
        long revision;
        Guid origin;
        lock (_stateLock)
        {
            enabled = _trafficProtectionEnabled;
            revision = _trafficProtectionRevision;
            _ = Guid.TryParse(_trafficProtectionOriginId, out origin);
        }

        var payload = new byte[ProtectionPayloadLength];
        payload[0] = ProtectionPayloadVersion;
        payload[1] = enabled ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(2, sizeof(long)), revision);
        origin.TryWriteBytes(payload.AsSpan(2 + sizeof(long), 16));
        return payload;
    }

    private void TryApplyProtectionPayload(byte[] payload)
    {
        if (payload.Length != ProtectionPayloadLength || payload[0] != ProtectionPayloadVersion) return;
        if (payload[1] > 1) return;
        var revision = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(2, sizeof(long)));
        if (revision < 0) return;
        var origin = new Guid(payload.AsSpan(2 + sizeof(long), 16)).ToString("D");
        var enabled = payload[1] == 1;

        var changed = false;
        lock (_stateLock)
        {
            _maxTrafficProtectionRevision = Math.Max(_maxTrafficProtectionRevision, revision);
            if (revision < _trafficProtectionRevision ||
                (revision == _trafficProtectionRevision &&
                 string.CompareOrdinal(origin, _trafficProtectionOriginId) <= 0))
            {
                return;
            }

            changed = _trafficProtectionEnabled != enabled;
            _trafficProtectionEnabled = enabled;
            _trafficProtectionRevision = revision;
            _trafficProtectionOriginId = origin;
        }

        _networkCoordinator.SetTrafficProtectionEnabled(enabled);
        if (changed) TrafficProtectionChanged?.Invoke(enabled);
    }

    private static string NormalizeOriginId(string? originId)
    {
        return Guid.TryParse(originId, out var parsed)
            ? parsed.ToString("D")
            : Guid.Empty.ToString("D");
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
        if (type is not (VoicePacketType.Hello or VoicePacketType.Ack or VoicePacketType.Audio or VoicePacketType.Goodbye)) return false;

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
        public VoicePeerRoute(
            IReadOnlyList<VoiceRouteTarget> candidates,
            VoiceRouteTarget? activeTarget)
        {
            Candidates = candidates;
            ActiveTarget = activeTarget;
        }

        public IReadOnlyList<VoiceRouteTarget> Candidates { get; set; }
        public VoiceRouteTarget? ActiveTarget { get; set; }
        public DateTimeOffset LastDiscoverySeenUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastPacketUtc { get; set; }
        public DateTimeOffset PendingHeartbeatSentUtc { get; set; }
        public int PendingHeartbeatSequence { get; set; }
        public int LastAudioSequence { get; set; }
        public double RoundTripMs { get; set; }
        public bool PresenceAnnounced { get; set; }
        public DateTimeOffset LastPresenceNotificationUtc { get; set; }
    }

    private enum VoicePacketType : byte
    {
        Hello = 1,
        Ack = 2,
        Audio = 3,
        Goodbye = 4
    }
}
