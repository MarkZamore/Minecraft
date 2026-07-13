using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class WorldTransferService : IAsyncDisposable
{
    public const int TransferPort = 35656;
    public const string ProtocolName = "MinecraftPortableWorld";
    public const int ProtocolVersion = 4;
    public const string TransferMessageType = "Transfer";
    public const string ProbeMessageType = "Probe";

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly MinecraftProcessService _minecraft;
    private readonly SettingsService _settingsService;
    private readonly WorldMetadataService _worldMetadata;
    private readonly LocalIdentityService _identityService;
    private readonly WorldPlayerProfileService _playerProfiles;
    private readonly WaypointSyncService _waypointSync;
    private readonly SkinService _skinService;
    private readonly LanRelayService _lanRelay;
    private readonly VoiceNetworkCoordinator _voiceNetwork;
    private readonly WorldTransferRuntimeOptions _runtimeOptions;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonSerializerOptions _indentedJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _listenerGate = new(1, 1);
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentDictionary<int, Task> _receiveTasks = new();
    private int _nextReceiveTaskId;
    private int _disposeState;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    public WorldTransferService(
        AppPaths paths,
        Logger logger,
        MinecraftProcessService minecraft,
        SettingsService settingsService,
        WorldMetadataService worldMetadata,
        LocalIdentityService identityService,
        WorldPlayerProfileService playerProfiles,
        WaypointSyncService waypointSync,
        SkinService skinService,
        LanRelayService lanRelay,
        VoiceNetworkCoordinator voiceNetwork,
        WorldTransferRuntimeOptions? runtimeOptions = null)
    {
        _paths = paths;
        _logger = logger;
        _minecraft = minecraft;
        _settingsService = settingsService;
        _worldMetadata = worldMetadata;
        _identityService = identityService;
        _playerProfiles = playerProfiles;
        _waypointSync = waypointSync;
        _skinService = skinService;
        _lanRelay = lanRelay;
        _voiceNetwork = voiceNetwork;
        _runtimeOptions = runtimeOptions ?? new WorldTransferRuntimeOptions();
        _runtimeOptions.Validate();
        WorldTransferRecoveryService.Recover(paths, logger);
    }

    public event Action<string>? StatusChanged;
    public event Action? BecameHost;
    public event Action<WorldTransferProgress>? ProgressChanged;
    public bool IsOperationActive => _transferGate.CurrentCount == 0;

    public async Task StartListenerAsync(AppSettings settings, CancellationToken token = default)
    {
        _shutdownCts.Token.ThrowIfCancellationRequested();
        await _listenerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopListenerCoreAsync().ConfigureAwait(false);
            var cts = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _listenerCts = cts;
            _listenerTask = ListenAsync(settings, ready, cts.Token);
            await ready.Task.WaitAsync(token).ConfigureAwait(false);
            _logger.Info("World transfer listener started.");
        }
        catch
        {
            await StopListenerCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    public async Task StopListenerAsync()
    {
        await _listenerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopListenerCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _listenerGate.Release();
        }
    }

    private async Task StopListenerCoreAsync()
    {
        var cts = _listenerCts;
        var task = _listenerTask;
        _listenerCts = null;
        _listenerTask = null;
        cts?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
        var receiveTasks = _receiveTasks.Values.ToArray();
        if (receiveTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(receiveTasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
        }
        cts?.Dispose();
    }

    public async Task SendWorldAsync(PeerViewModel peer, AppSettings settings, string worldPath, CancellationToken token)
    {
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
        token = operationCts.Token;
        EnsureMinecraftAvailableForTransfer("sender");
        await _transferGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            BeginProgress();
            try
            {
                var identity = ResolveIdentityContext(settings);
                if (peer.PackStatus != "OK")
                {
                    _logger.Warn($"Pack hash mismatch ({peer.PackStatus}); world transfer is allowed by local settings.");
                }

                var peerAddress = await VerifyPeerTransferReadyAsync(peer, settings, token);

                var worldDir = ResolveWorldToSend(worldPath);
                WorldAccessGuard.EnsureClosed(worldDir);
                StatusChanged?.Invoke("Saving personal waypoints...");
                await _waypointSync.FlushWorldAsync(worldDir, identity, token).ConfigureAwait(false);
                var worldName = Path.GetFileName(worldDir);
                var worldMetadata = _worldMetadata.Read(worldDir);
                var ownerId = ResolveOwnerIdentity(worldMetadata?.OwnerIdentityId, worldMetadata?.OwnerIdentityName, settings, identity.IdentityId, identity.IdentityName);
                var transferId = Guid.NewGuid().ToString("N");
                var transactionRoot = CreateTransactionDirectory(transferId);
                var stagingWorld = Path.Combine(transactionRoot, "staging-world");
                var archivePath = Path.Combine(transactionRoot, "world.zip");
                var escrowPath = Path.Combine(transactionRoot, "escrow", worldName);
                var journal = new WorldTransferJournal
                {
                    TransferId = transferId,
                    Role = "Sender",
                    State = "Preparing",
                    SourceWorldPath = worldDir,
                    EscrowPath = escrowPath
                };
                WriteJournal(transactionRoot, journal);
                var completed = false;

                try
                {
                    StatusChanged?.Invoke("Creating safe world snapshot...");
                    CopyWorldDirectory(worldDir, stagingWorld, token);
                    StatusChanged?.Invoke("Preparing player profiles...");
                    _playerProfiles.PrepareWorldForOutgoingTransfer(stagingWorld, identity);
                    var playerManifestSha = _playerProfiles.GetPlayerManifestHash(stagingWorld);
                    _waypointSync.Store.EnsureManifest(stagingWorld);
                    var waypointManifestSha = _waypointSync.Store.GetManifestHash(stagingWorld);

                    StatusChanged?.Invoke("Hashing world...");
                    var worldSha = HashDirectory(stagingWorld);

                    ZipFile.CreateFromDirectory(stagingWorld, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);

                    var fileInfo = new FileInfo(archivePath);
                    if (fileInfo.Length > settings.MaxArchiveBytes)
                    {
                        throw new InvalidOperationException("World archive exceeds configured size limit.");
                    }
                    RaiseProgress(0, fileInfo.Length);

                    var header = new WorldTransferHeader
                    {
                        Protocol = ProtocolName,
                        ProtocolVersion = ProtocolVersion,
                        MessageType = TransferMessageType,
                        TransferId = transferId,
                        SenderName = identity.IdentityName,
                        SenderIdentityId = identity.IdentityId,
                        SenderIdentityName = identity.IdentityName,
                        OwnerIdentityId = ownerId.id,
                        OwnerIdentityName = ownerId.name,
                        Size = fileInfo.Length,
                        WorldSha256 = worldSha,
                        PlayerManifestSha256 = playerManifestSha,
                        WaypointManifestSha256 = waypointManifestSha,
                        FileName = Path.GetFileName(archivePath),
                        WorldName = worldName
                    };

                    StatusChanged?.Invoke("Sending world archive...");
                    using var client = new TcpClient(peerAddress.AddressFamily);
                    await client.ConnectAsync(peerAddress, _runtimeOptions.Port, token);
                    await using var stream = client.GetStream();
                    await WriteJsonAsync(stream, header, token);
                    await using (var file = File.OpenRead(archivePath))
                    {
                        using var limiter = _voiceNetwork.CreateTransferLimiter();
                        await CopyWithProgressAsync(file, stream, fileInfo.Length, progress =>
                        {
                            RaiseProgress(progress, fileInfo.Length);
                        }, limiter, token);
                    }

                    var ready = await ReadJsonAsync<WorldTransferAck>(stream, token);
                    if (ready is null || !HasExpectedProtocol(ready.Protocol, ready.ProtocolVersion) ||
                        !ready.Ok || ready.Stage != "Ready" || ready.TransferId != transferId ||
                        !string.Equals(ready.WorldSha256, worldSha, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(ready.PlayerManifestSha256, playerManifestSha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Receiver rejected world archive: {ready?.Message ?? "no ready acknowledgement"}");
                    }
                    if (!string.Equals(ready.WaypointManifestSha256, waypointManifestSha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Receiver rejected world archive: {ready?.Message ?? "no ready acknowledgement"}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(escrowPath)!);
                    Directory.Move(worldDir, escrowPath);
                    journal.State = "Escrowed";
                    WriteJournal(transactionRoot, journal);
                    journal.State = "CommitSent";
                    WriteJournal(transactionRoot, journal);
                    await WriteJsonAsync(stream, new WorldTransferControl
                    {
                        Protocol = ProtocolName,
                        ProtocolVersion = ProtocolVersion,
                        TransferId = transferId,
                        Command = "Commit"
                    }, token);

                    var committed = await ReadJsonAsync<WorldTransferAck>(stream, token);
                    if (committed is null || !HasExpectedProtocol(committed.Protocol, committed.ProtocolVersion) ||
                        !committed.Ok || committed.Stage != "Committed" || committed.TransferId != transferId ||
                        !string.Equals(committed.WorldSha256, worldSha, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(committed.PlayerManifestSha256) ||
                        !string.Equals(committed.WaypointManifestSha256, waypointManifestSha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Transfer commit was not confirmed. The source world remains safely quarantined in Personal\\Transfers.");
                    }

                    journal.State = "Committed";
                    WriteJournal(transactionRoot, journal);
                    completed = true;
                    var selectedRelativePath = Path.GetRelativePath(_paths.Worlds, worldDir);
                    if (string.Equals(settings.SelectedWorldRelativePath, selectedRelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        settings.SelectedWorldRelativePath = "";
                    }

                    _logger.Info("World archive transferred successfully; local source world removed.");
                }
                catch
                {
                    if (journal.State != "CommitSent" && Directory.Exists(escrowPath) && !Directory.Exists(worldDir))
                    {
                        Directory.Move(escrowPath, worldDir);
                    }
                    throw;
                }
                finally
                {
                    if (completed || journal.State != "CommitSent") DeleteDirectoryIfExists(transactionRoot);
                }
            }
            finally
            {
                EndProgress();
            }
        }
        finally
        {
            _transferGate.Release();
        }
    }

    private async Task ListenAsync(
        AppSettings settings,
        TaskCompletionSource ready,
        CancellationToken token)
    {
        TcpListener? listener = null;
        try
        {
            var listenAddress = _runtimeOptions.ListenAddress.Equals(IPAddress.Any)
                ? IPAddress.IPv6Any
                : _runtimeOptions.ListenAddress;
            listener = CreateListener(listenAddress, _runtimeOptions.Port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex) when (listenAddress.Equals(IPAddress.IPv6Any))
            {
                listener.Stop();
                _logger.Warn($"Dual-stack transfer listener is unavailable; using IPv4: {ex.Message}");
                listener = CreateListener(IPAddress.Any, _runtimeOptions.Port);
                listener.Start();
            }
            ready.TrySetResult();
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                var id = Interlocked.Increment(ref _nextReceiveTaskId);
                var receiveTask = HandleIncomingClientAsync(client, settings, token);
                _receiveTasks[id] = receiveTask;
                _ = receiveTask.ContinueWith(
                    completedTask => _receiveTasks.TryRemove(id, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            _logger.Warn($"World transfer listener failed: {ex.Message}");
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static TcpListener CreateListener(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            listener.Server.DualMode = true;
        }
        return listener;
    }

    private async Task HandleIncomingClientAsync(TcpClient client, AppSettings settings, CancellationToken token)
    {
        using var clientScope = client;
        try
        {
            await using var stream = client.GetStream();
            var initialFrame = await PortableProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false);
            var protocol = PortableProtocol.ReadProtocol(initialFrame);
            if (string.Equals(protocol, WaypointSyncService.ProtocolName, StringComparison.Ordinal))
            {
                await _waypointSync.HandleIncomingAsync(stream, initialFrame, token).ConfigureAwait(false);
                return;
            }
            if (string.Equals(protocol, SkinService.ProtocolName, StringComparison.Ordinal))
            {
                await _skinService.HandleIncomingAsync(stream, initialFrame, token).ConfigureAwait(false);
                return;
            }
            if (string.Equals(protocol, LanRelayService.ProtocolName, StringComparison.Ordinal))
            {
                await _lanRelay.HandleIncomingAsync(stream, initialFrame, token).ConfigureAwait(false);
                return;
            }
            await ReceiveWorldAsync(stream, settings, initialFrame, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException)
        {
            _logger.Warn($"Incoming portable protocol request was rejected: {ex.Message}");
        }
    }

    private async Task ReceiveWorldAsync(Stream stream, AppSettings settings, byte[] initialFrame, CancellationToken token)
    {
        var identity = ResolveIdentityContext(settings);
        WorldTransferHeader? header = null;
        string? transactionRoot = null;
        string? receivedPath = null;
        string? tempWorldPath = null;
        WorldTransferJournal? journal = null;
        var operationAcquired = false;
        var progressStarted = false;
        try
        {
            header = PortableProtocol.Deserialize<WorldTransferHeader>(initialFrame, _jsonOptions)
                ?? throw new InvalidOperationException("Invalid transfer header.");
            if (!HasExpectedProtocol(header.Protocol, header.ProtocolVersion))
            {
                throw new InvalidOperationException("The sender uses an incompatible world transfer protocol.");
            }
            if (header.MessageType == ProbeMessageType)
            {
                var available = _transferGate.CurrentCount > 0 &&
                                !_minecraft.IsClientRunning &&
                                !_minecraft.IsClientPreparing;
                await WriteJsonAsync(stream, new WorldTransferAck
                {
                    Protocol = ProtocolName,
                    ProtocolVersion = ProtocolVersion,
                    Ok = available,
                    Stage = "Probe",
                    Message = available
                        ? "ready"
                        : _minecraft.IsClientRunning
                            ? "Minecraft is running on the receiver"
                            : _minecraft.IsClientPreparing
                                ? "Minecraft is being prepared on the receiver"
                            : "another world transfer is active"
                }, token);
                return;
            }
            if (header.MessageType != TransferMessageType ||
                !Guid.TryParseExact(header.TransferId, "N", out var parsedTransferId))
            {
                throw new InvalidOperationException("The sender uses an incompatible world transfer protocol.");
            }
            operationAcquired = await _transferGate.WaitAsync(0, token).ConfigureAwait(false);
            if (!operationAcquired)
            {
                throw new InvalidOperationException("Another world transfer is already active.");
            }
            EnsureMinecraftAvailableForTransfer("receiver");
            if (header.Size <= 0 || header.Size > settings.MaxArchiveBytes ||
                string.IsNullOrWhiteSpace(header.WorldSha256) ||
                string.IsNullOrWhiteSpace(header.PlayerManifestSha256) ||
                string.IsNullOrWhiteSpace(header.WaypointManifestSha256))
            {
                throw new InvalidOperationException("Transfer header is incomplete or exceeds the configured limit.");
            }

            transactionRoot = CreateTransactionDirectory(header.TransferId);
            journal = new WorldTransferJournal
            {
                TransferId = header.TransferId,
                Role = "Receiver",
                State = "Receiving"
            };
            WriteJournal(transactionRoot, journal);
            receivedPath = Path.Combine(transactionRoot, "received.zip");
            BeginProgress(header.Size);
            progressStarted = true;
            await using (var file = new FileStream(receivedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var limiter = _voiceNetwork.CreateTransferLimiter();
                await CopyExactlyWithLimitWithProgressAsync(stream, file, header.Size, settings.MaxArchiveBytes, progress =>
                {
                    RaiseProgress(progress, header.Size);
                }, limiter, token);
            }

            tempWorldPath = Path.Combine(transactionRoot, "staging-world");
            Directory.CreateDirectory(tempWorldPath);
            await Task.Run(() => ZipFile.ExtractToDirectory(receivedPath, tempWorldPath), token);
            if (!IsMinecraftWorldDirectory(tempWorldPath))
            {
                throw new InvalidOperationException("Received archive does not contain a Minecraft world.");
            }

            var receivedWorldSha = HashDirectory(tempWorldPath);
            if (!string.Equals(receivedWorldSha, header.WorldSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("World SHA256 mismatch after extraction.");
            }
            _playerProfiles.ValidatePlayerManifest(tempWorldPath);
            var sourceManifestSha = _playerProfiles.GetPlayerManifestHash(tempWorldPath);
            if (!string.Equals(sourceManifestSha, header.PlayerManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Player manifest SHA256 mismatch after extraction.");
            }
            _waypointSync.Store.ValidateManifest(tempWorldPath);
            var waypointManifestSha = _waypointSync.Store.GetManifestHash(tempWorldPath);
            if (!string.Equals(waypointManifestSha, header.WaypointManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Waypoint manifest SHA256 mismatch after extraction.");
            }

            StatusChanged?.Invoke("Preparing player profile...");
            _playerProfiles.PrepareReceivedWorldForIdentity(tempWorldPath, identity);
            _playerProfiles.ValidatePlayerManifest(tempWorldPath);
            var installedManifestSha = _playerProfiles.GetPlayerManifestHash(tempWorldPath);
            var owner = ResolveOwnerIdentity(null, null, settings, identity.IdentityId, identity.IdentityName, header.OwnerIdentityId, header.OwnerIdentityName);
            if (!_worldMetadata.TryWriteOwnerMetadata(tempWorldPath, owner.id, owner.name, overwriteExistingOwner: false))
            {
                throw new InvalidOperationException("Could not preserve world creator metadata.");
            }
            if (!_worldMetadata.TryWriteCurrentHolderMetadata(tempWorldPath, identity.IdentityId, identity.IdentityName, transferred: true))
            {
                throw new InvalidOperationException("Could not update current world holder metadata.");
            }

            EnsureMinecraftAvailableForTransfer("receiver");
            journal.State = "Ready";
            WriteJournal(transactionRoot, journal);
            await WriteJsonAsync(stream, new WorldTransferAck
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                Ok = true,
                Stage = "Ready",
                TransferId = header.TransferId,
                Message = "ready",
                WorldSha256 = receivedWorldSha,
                PlayerManifestSha256 = sourceManifestSha,
                WaypointManifestSha256 = waypointManifestSha
            }, token);
            var control = await ReadJsonAsync<WorldTransferControl>(stream, token);
            if (control is null || !HasExpectedProtocol(control.Protocol, control.ProtocolVersion) ||
                control.TransferId != header.TransferId || control.Command != "Commit")
            {
                throw new InvalidOperationException("World transfer commit command is invalid.");
            }

            journal.State = "CommitReceived";
            WriteJournal(transactionRoot, journal);
            EnsureMinecraftAvailableForTransfer("receiver");
            var installedWorldPath = InstallReceivedWorld(tempWorldPath, header.WorldName);
            tempWorldPath = null;
            journal.State = "Installed";
            journal.InstalledWorldPath = installedWorldPath;
            WriteJournal(transactionRoot, journal);
            await WriteJsonAsync(stream, new WorldTransferAck
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                Ok = true,
                Stage = "Committed",
                TransferId = header.TransferId,
                Message = "accepted",
                WorldSha256 = receivedWorldSha,
                PlayerManifestSha256 = installedManifestSha,
                WaypointManifestSha256 = waypointManifestSha
            }, token);
            journal.State = "Committed";
            WriteJournal(transactionRoot, journal);
            settings.SelectedWorldRelativePath = Path.GetRelativePath(_paths.Worlds, installedWorldPath);
            _settingsService.Save(settings);
            BecameHost?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"World receive failed: {ex.Message}");
            try
            {
                await WriteJsonAsync(stream, new WorldTransferAck
                {
                    Protocol = ProtocolName,
                    ProtocolVersion = ProtocolVersion,
                    Ok = false,
                    Stage = "Rejected",
                    TransferId = header?.TransferId ?? string.Empty,
                    Message = ex.Message,
                    WorldSha256 = header?.WorldSha256 ?? string.Empty,
                    WaypointManifestSha256 = header?.WaypointManifestSha256 ?? string.Empty
                }, CancellationToken.None);
            }
            catch
            {
            }
        }
        finally
        {
            if (progressStarted) EndProgress();
            DeleteFileIfExists(receivedPath);
            DeleteDirectoryIfExists(tempWorldPath);
            if (transactionRoot is not null) DeleteDirectoryIfExists(transactionRoot);
            if (operationAcquired) _transferGate.Release();
        }
    }

    private string InstallReceivedWorld(string extractedWorldPath, string? worldName)
    {
        var safeWorldName = GetSafeWorldName(worldName);
        var worldDir = GetAvailableWorldDirectory(safeWorldName);
        _paths.EnsureUnderRoot(worldDir);
        Directory.CreateDirectory(_paths.Worlds);
        Directory.Move(extractedWorldPath, worldDir);
        _logger.Info($"Received world installed: {Path.GetFileName(worldDir)}.");
        return worldDir;
    }

    private async Task<IPAddress> VerifyPeerTransferReadyAsync(PeerViewModel peer, AppSettings settings, CancellationToken token)
    {
        var identity = _identityService.ResolveContext(settings);
        var candidateAddresses = peer.GetCandidateAddresses()
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null)
            .Cast<IPAddress>()
            .Distinct()
            .ToArray();
        if (candidateAddresses.Length == 0)
        {
            throw new InvalidOperationException("Selected player does not have a valid network IP address.");
        }

        var failures = new List<string>();
        foreach (var ip in candidateAddresses)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var client = new TcpClient(ip.AddressFamily);
                await client.ConnectAsync(ip, _runtimeOptions.Port, timeoutCts.Token);
                await using var stream = client.GetStream();
                await WriteJsonAsync(stream, new WorldTransferHeader
                {
                    Protocol = ProtocolName,
                    ProtocolVersion = ProtocolVersion,
                    MessageType = ProbeMessageType,
                    SenderName = identity.IdentityName,
                    SenderIdentityId = identity.IdentityId,
                    SenderIdentityName = identity.IdentityName,
                    Size = 0,
                    FileName = "probe",
                    WorldName = ""
                }, timeoutCts.Token);

                var ack = await ReadJsonAsync<WorldTransferAck>(stream, timeoutCts.Token);
                if (ack is null || !HasExpectedProtocol(ack.Protocol, ack.ProtocolVersion) ||
                    !ack.Ok || ack.Stage != "Probe")
                {
                    throw new InvalidOperationException(ack?.Message ?? "receiver did not accept transfer probe");
                }
                return ip;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException or InvalidOperationException)
            {
                token.ThrowIfCancellationRequested();
                failures.Add($"{ip}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            BuildPeerConnectionMessage(string.Join(", ", candidateAddresses.Select(address => address.ToString()))) +
            Environment.NewLine + string.Join(Environment.NewLine, failures.Take(3)));
    }

    private void RaiseProgress(long current, long total)
    {
        try
        {
            ProgressChanged?.Invoke(new WorldTransferProgress(true, current, total));
        }
        catch
        {
        }
    }

    private void EnsureMinecraftAvailableForTransfer(string role)
    {
        if (!_minecraft.IsClientRunning && !_minecraft.IsClientPreparing) return;
        throw new InvalidOperationException(
            $"Minecraft is running or being prepared on the transfer {role}.");
    }

    private static bool HasExpectedProtocol(string? protocol, int version) =>
        string.Equals(protocol, ProtocolName, StringComparison.Ordinal) && version == ProtocolVersion;

    private void BeginProgress(long total = 0) => RaiseProgress(0, total);

    private void EndProgress()
    {
        try
        {
            ProgressChanged?.Invoke(new WorldTransferProgress(false, 0, 0));
        }
        catch
        {
        }
    }

    private string GetTransferTempDirectory()
    {
        var path = Path.Combine(_paths.Personal, "Transfers");
        _paths.EnsureUnderRoot(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private LocalIdentityContext ResolveIdentityContext(AppSettings settings)
    {
        return _identityService.ResolveContext(settings);
    }

    private string CreateTransactionDirectory(string transferId)
    {
        if (!Guid.TryParseExact(transferId, "N", out _)) throw new InvalidDataException("Transfer ID is invalid.");
        var path = Path.Combine(GetTransferTempDirectory(), transferId);
        _paths.EnsureUnderRoot(path);
        if (Directory.Exists(path)) throw new IOException("Transfer transaction already exists.");
        Directory.CreateDirectory(path);
        return path;
    }

    private void WriteJournal(string transactionRoot, WorldTransferJournal journal)
    {
        journal.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AtomicFile.WriteAllText(
            Path.Combine(transactionRoot, "transaction.json"),
            JsonSerializer.Serialize(journal, _indentedJsonOptions));
    }

    private static void CopyWorldDirectory(string sourceRoot, string destinationRoot, CancellationToken token)
    {
        Directory.CreateDirectory(destinationRoot);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (string.Equals(name, "session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(destination, name);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"World contains an unsupported filesystem link: {entry}");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(target);
                    pending.Push((entry, target));
                }
                else
                {
                    File.Copy(entry, target, overwrite: false);
                }
            }
        }
    }

    private string BuildPeerConnectionMessage(string peerIp)
    {
        return $"""
        Could not connect to player {peerIp}:{_runtimeOptions.Port}.

        Check on the receiving computer:
        1. Minecraft.exe is running.
        2. Both players are in the same virtual or local network.
        3. The player list shows the correct network IP.
        4. Windows Firewall allows incoming TCP connections on port {_runtimeOptions.Port}.

        If Windows asks for Minecraft.exe network access, allow private networks.
        """;
    }

    private string ResolveWorldToSend(string worldPath)
    {
        Directory.CreateDirectory(_paths.Worlds);

        if (string.IsNullOrWhiteSpace(worldPath))
        {
            throw new DirectoryNotFoundException("Choose a world to transfer.");
        }

        var world = Path.GetFullPath(worldPath);
        _paths.EnsureUnderRoot(world);

        var worldsRoot = _paths.Worlds.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!world.StartsWith(worldsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Selected world must be inside ./Minecraft/Worlds.");
        }

        if (!IsMinecraftWorldDirectory(world))
        {
            throw new DirectoryNotFoundException("Selected folder is not a Minecraft world.");
        }

        _logger.Info($"Selected world for transfer: {Path.GetFileName(world)}.");
        return world;
    }

    private const string UnknownIdentityName = "\u043D\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u043D\u043E";

    private static (string id, string name) ResolveOwnerIdentity(
        string? metadataOwnerId,
        string? metadataOwnerName,
        AppSettings settings,
        string? localOwnerId,
        string? localOwnerName,
        string? headerOwnerId = null,
        string? headerOwnerName = null)
    {
        var resolvedHeaderId = !string.IsNullOrWhiteSpace(headerOwnerId) ? headerOwnerId.Trim() : null;
        var resolvedHeaderName = !string.IsNullOrWhiteSpace(headerOwnerName) ? headerOwnerName.Trim() : null;
        var resolvedLocalId = !string.IsNullOrWhiteSpace(localOwnerId) ? localOwnerId.Trim() : string.Empty;
        var resolvedLocalName = !string.IsNullOrWhiteSpace(localOwnerName) ? localOwnerName.Trim() : string.Empty;

        if (!string.IsNullOrWhiteSpace(resolvedHeaderId) || !string.IsNullOrWhiteSpace(resolvedHeaderName))
        {
            var headerName = resolvedHeaderName;
            if (!string.IsNullOrWhiteSpace(resolvedLocalId) &&
                string.Equals(resolvedHeaderId, resolvedLocalId, StringComparison.OrdinalIgnoreCase))
            {
                headerName = string.IsNullOrWhiteSpace(resolvedLocalName) ? UnknownIdentityName : resolvedLocalName;
            }

            return (resolvedHeaderId ?? string.Empty, headerName ?? UnknownIdentityName);
        }

        var resolvedMetadataId = !string.IsNullOrWhiteSpace(metadataOwnerId) ? metadataOwnerId.Trim() : null;
        var resolvedMetadataName = !string.IsNullOrWhiteSpace(metadataOwnerName) ? metadataOwnerName.Trim() : null;
        if (!string.IsNullOrWhiteSpace(resolvedMetadataId) || !string.IsNullOrWhiteSpace(resolvedMetadataName))
        {
            var metadataName = resolvedMetadataName;
            if (!string.IsNullOrWhiteSpace(resolvedLocalId) &&
                string.Equals(resolvedMetadataId, resolvedLocalId, StringComparison.OrdinalIgnoreCase))
            {
                metadataName = string.IsNullOrWhiteSpace(resolvedLocalName) ? UnknownIdentityName : resolvedLocalName;
            }

            return (resolvedMetadataId ?? string.Empty, metadataName ?? UnknownIdentityName);
        }

        var settingsId = string.IsNullOrWhiteSpace(localOwnerId) ? string.Empty : localOwnerId.Trim();
        var settingsName = string.IsNullOrWhiteSpace(localOwnerName) ? UnknownIdentityName : localOwnerName.Trim();

        return (settingsId, settingsName);
    }

    public static bool IsMinecraftWorldDirectory(string path)
    {
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "level.dat"));
    }

    private static DateTime GetWorldLastWriteTimeUtc(string path)
    {
        var levelDat = Path.Combine(path, "level.dat");
        return File.Exists(levelDat) ? File.GetLastWriteTimeUtc(levelDat) : Directory.GetLastWriteTimeUtc(path);
    }

    private string GetAvailableWorldDirectory(string safeWorldName)
    {
        var basePath = Path.Combine(_paths.Worlds, safeWorldName);
        if (!Directory.Exists(basePath) && !File.Exists(basePath))
        {
            return basePath;
        }

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(_paths.Worlds, $"{safeWorldName} ({index})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string GetSafeWorldName(string? worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return "World";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safe = new string(worldName.Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "World" : safe;
    }

    private void DeleteTransferredWorld(string worldDir)
    {
        _paths.EnsureUnderRoot(worldDir);
        var worldsRoot = _paths.Worlds.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullWorldDir = Path.GetFullPath(worldDir);
        if (!fullWorldDir.StartsWith(worldsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a world outside ./Minecraft/Worlds.");
        }

        try
        {
            Directory.Delete(fullWorldDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("World was transferred and verified, but the local source world could not be deleted. Close Minecraft and retry the transfer so only one host remains.", ex);
        }
    }

    private static string HashDirectory(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(fullRoot, path).Replace('\\', '/'), StringComparer.Ordinal)
            .ToList();

        var buffer = new byte[1024 * 1024];
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
            var relativeBytes = Encoding.UTF8.GetBytes(relativePath);
            AppendInt64(hash, relativeBytes.Length);
            hash.AppendData(relativeBytes);

            var fileInfo = new FileInfo(file);
            AppendInt64(hash, fileInfo.Length);

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static async Task CopyExactlyWithLimitAsync(Stream input, Stream output, long size, long maxSize, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (total < size)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - total)), token);
            if (read == 0) throw new EndOfStreamException("Transfer ended early.");
            total += read;
            if (total > maxSize) throw new InvalidOperationException("Transfer size exceeds configured limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static async Task CopyExactlyWithLimitWithProgressAsync(
        Stream input,
        Stream output,
        long size,
        long maxSize,
        Action<long> progress,
        VoiceTransferLimiter limiter,
        CancellationToken token)
    {
        var buffer = new byte[VoiceTransferLimiter.TransferBlockSize];
        long total = 0;
        while (total < size)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - total)), token);
            if (read == 0) throw new EndOfStreamException("Transfer ended early.");
            total += read;
            if (total > maxSize) throw new InvalidOperationException("Transfer size exceeds configured limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            progress(total);
            await limiter.ThrottleAsync(read, token).ConfigureAwait(false);
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream input,
        Stream output,
        long totalSize,
        Action<long> progress,
        VoiceTransferLimiter limiter,
        CancellationToken token)
    {
        var buffer = new byte[VoiceTransferLimiter.TransferBlockSize];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, totalSize - total)), token);
            if (read <= 0) break;
            total += read;
            if (total > totalSize) throw new InvalidOperationException("Transfer size exceeds expected archive size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            progress(total);
            await limiter.ThrottleAsync(read, token).ConfigureAwait(false);
        }

        if (total != totalSize)
        {
            throw new InvalidOperationException("Transfer data size mismatch.");
        }
    }

    private async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken token)
    {
        await PortableProtocol.WriteJsonAsync(stream, value, _jsonOptions, token).ConfigureAwait(false);
    }

    private async Task<T?> ReadJsonAsync<T>(Stream stream, CancellationToken token)
    {
        var bytes = await PortableProtocol.ReadFrameAsync(stream, token).ConfigureAwait(false);
        return PortableProtocol.Deserialize<T>(bytes, _jsonOptions);
    }

    private static void DeleteFileIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
    }

    private static void DeleteDirectoryIfExists(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        _shutdownCts.Cancel();
        await StopListenerAsync().ConfigureAwait(false);
        await _transferGate.WaitAsync().ConfigureAwait(false);
        _transferGate.Release();
        _listenerGate.Dispose();
        _transferGate.Dispose();
        _shutdownCts.Dispose();
    }
}

public sealed record WorldTransferProgress(bool IsActive, long Current, long Total);

public sealed class WorldTransferRuntimeOptions
{
    public int Port { get; init; } = WorldTransferService.TransferPort;
    public IPAddress ListenAddress { get; init; } = IPAddress.IPv6Any;
    internal void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "World transfer port must be between 1 and 65535.");
        }
        ArgumentNullException.ThrowIfNull(ListenAddress);
    }
}
