using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Minecraft;

public sealed class WaypointSyncService : IAsyncDisposable
{
    public const string ProtocolName = "MinecraftPortableWaypoints";
    public const int ProtocolVersion = 1;
    public const string PullMessageType = "Pull";
    public const string PushMessageType = "Push";

    private static readonly TimeSpan PeriodicSyncInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RemoteSessionTtl = TimeSpan.FromSeconds(15);

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly WorldMetadataService _worldMetadata;
    private readonly WaypointProviderRegistry _providerRegistry;
    private readonly WaypointStoreService _store;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, RemoteWorldSession> _remoteSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingHostAnnouncement> _pendingAnnouncements = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Task _syncLoopTask;
    private LocalWaypointSession? _localSession;
    private HostedWorldSession? _hostedSession;
    private HostedWorldSession? _pendingHostedSnapshot;
    private WaypointLocalState _localState = new();
    private DateTimeOffset _lastSyncUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRelevantChangeUtc = DateTimeOffset.MinValue;
    private bool _scanRequested;
    private int _disposeState;

    public WaypointSyncService(
        AppPaths paths,
        Logger logger,
        WorldMetadataService worldMetadata)
    {
        _paths = paths;
        _logger = logger;
        _worldMetadata = worldMetadata;
        _providerRegistry = new WaypointProviderRegistry(logger);
        _store = new WaypointStoreService(worldMetadata, _providerRegistry, logger);
        MigrateLegacyState();
        _localState = LoadLocalState();
        _syncLoopTask = Task.Run(() => SyncLoopAsync(_shutdownCts.Token));
    }

    public WaypointStoreService Store => _store;

    public async Task PrepareForLaunchAsync(
        string packRelativePath,
        LocalIdentityContext identity,
        CancellationToken token)
    {
        await _syncGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var packDirectory = _paths.CombineUnderPacks(packRelativePath);
            var gameDirectory = _paths.CombineUnderInstances(packRelativePath);
            var providers = _providerRegistry.Detect(packDirectory);
            var session = new LocalWaypointSession(
                packRelativePath,
                gameDirectory,
                identity,
                providers);
            lock (_stateGate)
            {
                _localSession = session;
                _remoteSessions.Clear();
                _hostedSession = null;
                _pendingHostedSnapshot = null;
            }
            ConfigureWatchers(session);

            foreach (var worldPath in EnumerateWorlds())
            {
                token.ThrowIfCancellationRequested();
                string worldId;
                try
                {
                    worldId = _worldMetadata.EnsureWorldId(worldPath, CreateMetadataContext(session, identity, packHash: string.Empty));
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Waypoint preparation skipped world {Path.GetFileName(worldPath)}: {ex.Message}");
                    continue;
                }

                foreach (var (provider, modVersion) in providers)
                {
                    var contextId = provider.ReadWorldContextId(worldPath);
                    if (string.IsNullOrWhiteSpace(contextId)) continue;
                    var stored = _store.ReadSnapshot(worldPath, identity.IdentityId, provider.ProviderId);
                    if (stored is null) continue;
                    try
                    {
                        var nativeContext = new WaypointNativeContext(
                            gameDirectory,
                            worldPath,
                            worldId,
                            contextId,
                            modVersion,
                            IsHost: true,
                            RemoteAddress: null);
                        ImportSnapshot(provider, nativeContext, identity.IdentityId, stored);
                    }
                    catch (Exception ex)
                    {
                        SaveConflict(worldId, identity.IdentityId, provider.ProviderId, stored.Snapshot, $"prepare-{ex.GetType().Name}");
                        _logger.Warn($"Could not restore {provider.ProviderId} waypoints for {Path.GetFileName(worldPath)}: {ex.Message}");
                    }
                }
            }

            PendingHostAnnouncement[] pending;
            lock (_stateGate)
            {
                var cutoff = DateTimeOffset.UtcNow - RemoteSessionTtl;
                pending = _pendingAnnouncements.Values.Where(item => item.LastSeenUtc >= cutoff).ToArray();
            }
            foreach (var announcement in pending)
            {
                RegisterRemote(announcement, session);
            }

            await SyncCoreAsync(token).ConfigureAwait(false);
            RequestScan();
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public void UpdateHostingState(
        bool lanOpen,
        string packRelativePath,
        string packHash,
        LocalIdentityContext identity)
    {
        LocalWaypointSession? local;
        lock (_stateGate) local = _localSession;
        if (!lanOpen || local is null ||
            !string.Equals(local.PackRelativePath, packRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            QueueHostedSnapshotAndClear();
            return;
        }

        var worldPath = EnumerateWorlds()
            .Where(WorldAccessGuard.IsOpen)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(Path.Combine(path, "level.dat")))
            .FirstOrDefault();
        if (worldPath is null)
        {
            QueueHostedSnapshotAndClear();
            return;
        }

        try
        {
            var hosted = CreateHostedSession(local, worldPath, identity, packHash);
            lock (_stateGate)
            {
                var changed = _hostedSession is null ||
                              !string.Equals(_hostedSession.WorldId, hosted.WorldId, StringComparison.OrdinalIgnoreCase) ||
                              !string.Equals(_hostedSession.WorldPath, hosted.WorldPath, StringComparison.OrdinalIgnoreCase);
                if (changed && _hostedSession is not null)
                {
                    _pendingHostedSnapshot = _hostedSession;
                }
                _hostedSession = hosted;
                if (changed)
                {
                    _scanRequested = true;
                    _lastRelevantChangeUtc = DateTimeOffset.UtcNow - ChangeDebounce;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_stateGate) _hostedSession = null;
            _logger.Warn($"Could not prepare waypoint hosting state: {ex.Message}");
        }
    }

    public WaypointHostAdvertisement? GetHostAdvertisement()
    {
        lock (_stateGate)
        {
            if (_hostedSession is null) return null;
            return new WaypointHostAdvertisement(
                _hostedSession.WorldId,
                _hostedSession.Providers.Select(provider => new WaypointProviderAnnouncement
                {
                    ProviderId = provider.Provider.ProviderId,
                    ModVersion = provider.ModVersion,
                    WorldContextId = provider.WorldContextId
                }).ToArray());
        }
    }

    public void ObservePeer(PeerAnnouncement announcement)
    {
        if (!announcement.IsHost || announcement.WaypointProtocolVersion != ProtocolVersion ||
            !Guid.TryParse(announcement.HostedWorldId, out var worldId) || worldId == Guid.Empty ||
            !IPAddress.TryParse(announcement.NetworkAddress, out var address))
        {
            return;
        }

        var identityKey = string.IsNullOrWhiteSpace(announcement.IdentityId)
            ? address.ToString()
            : announcement.IdentityId;
        var pending = new PendingHostAnnouncement(
            worldId.ToString("D"),
            identityKey,
            address.ToString(),
            (announcement.WaypointProviders ?? []).Select(item => new WaypointProviderAnnouncement
            {
                ProviderId = item.ProviderId,
                ModVersion = item.ModVersion,
                WorldContextId = item.WorldContextId
            }).ToArray(),
            DateTimeOffset.UtcNow);

        LocalWaypointSession? local;
        lock (_stateGate)
        {
            var pendingKey = $"{pending.WorldId}|{pending.HostIdentityId}|{pending.Address}";
            _pendingAnnouncements[pendingKey] = pending;
            local = _localSession;
        }
        if (local is null) return;
        RegisterRemote(pending, local);
    }

    private void RegisterRemote(PendingHostAnnouncement announcement, LocalWaypointSession local)
    {
        var supported = local.Providers.ToDictionary(item => item.Provider.ProviderId, StringComparer.OrdinalIgnoreCase);
        var providers = announcement.Providers
            .Where(item => supported.ContainsKey(item.ProviderId) && !string.IsNullOrWhiteSpace(item.WorldContextId))
            .Select(item => new RemoteProvider(
                supported[item.ProviderId].Provider,
                supported[item.ProviderId].ModVersion,
                item.WorldContextId))
            .ToDictionary(item => item.Provider.ProviderId, StringComparer.OrdinalIgnoreCase);
        if (providers.Count == 0) return;

        var key = $"{announcement.WorldId}|{announcement.HostIdentityId}";
        lock (_stateGate)
        {
            if (!_remoteSessions.TryGetValue(key, out var remote))
            {
                remote = new RemoteWorldSession(announcement.WorldId);
                _remoteSessions[key] = remote;
            }
            var endpointAdded = !remote.Addresses.ContainsKey(announcement.Address);
            var providersChanged = remote.Providers.Count != providers.Count || providers.Any(item =>
                !remote.Providers.TryGetValue(item.Key, out var current) ||
                !string.Equals(current.WorldContextId, item.Value.WorldContextId, StringComparison.Ordinal));
            remote.Addresses[announcement.Address] = announcement.LastSeenUtc;
            remote.Providers = providers;
            if (endpointAdded || providersChanged)
            {
                remote.PullRequested = true;
                remote.ChangeVersion++;
                _scanRequested = true;
                _lastRelevantChangeUtc = DateTimeOffset.UtcNow - ChangeDebounce;
            }
        }
    }

    public async Task FlushAsync(CancellationToken token = default)
    {
        await _syncGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await SyncCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task FlushWorldAsync(string worldPath, LocalIdentityContext identity, CancellationToken token)
    {
        await _syncGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            HostedWorldSession? hosted;
            LocalWaypointSession? local;
            lock (_stateGate)
            {
                hosted = _hostedSession;
                local = _localSession;
            }
            local = ResolveLocalSessionForWorld(worldPath, identity, local);
            if (local is not null)
            {
                if (hosted is null ||
                    !string.Equals(Path.GetFullPath(hosted.WorldPath), Path.GetFullPath(worldPath), StringComparison.OrdinalIgnoreCase))
                {
                    hosted = CreateHostedSession(local, worldPath, identity, packHash: string.Empty);
                }
                SyncHostedCore(local, hosted, identity);
            }
            _store.EnsureManifest(worldPath);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task HandleIncomingAsync(
        Stream stream,
        byte[] initialFrame,
        CancellationToken token)
    {
        WaypointSyncEnvelope? request = null;
        try
        {
            request = PortableProtocol.Deserialize<WaypointSyncEnvelope>(initialFrame, _jsonOptions)
                ?? throw new InvalidDataException("Invalid waypoint sync request.");
            ValidateRequest(request);
            HostedWorldSession hosted;
            lock (_stateGate)
            {
                hosted = _hostedSession
                    ?? throw new InvalidOperationException("No LAN world is currently available for waypoint synchronization.");
            }
            if (!string.Equals(hosted.WorldId, request.WorldId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Requested waypoint world is not currently hosted.");
            }
            var provider = hosted.Providers.FirstOrDefault(item =>
                string.Equals(item.Provider.ProviderId, request.ProviderId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.WorldContextId, request.WorldContextId, StringComparison.Ordinal));
            if (provider is null)
            {
                throw new InvalidOperationException("Requested waypoint provider is not active in the hosted world.");
            }

            if (request.MessageType == PullMessageType)
            {
                var stored = _store.ReadSnapshot(hosted.WorldPath, request.PlayerUuid, request.ProviderId);
                await PortableProtocol.WriteJsonAsync(stream, new WaypointSyncReply
                {
                    Ok = true,
                    Message = stored is null ? "empty" : "ready",
                    Revision = stored?.Revision ?? 0,
                    Sha256 = stored?.Sha256 ?? string.Empty,
                    Snapshot = stored?.Snapshot
                }, _jsonOptions, token).ConfigureAwait(false);
                return;
            }

            if (request.MessageType != PushMessageType || request.Snapshot is null)
            {
                throw new InvalidDataException("Unsupported waypoint sync message type.");
            }
            provider.Provider.Validate(request.Snapshot);
            if (!string.Equals(request.Snapshot.WorldContextId, provider.WorldContextId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Waypoint snapshot has a different world context.");
            }
            var result = _store.SaveIncomingSnapshot(
                hosted.WorldPath,
                request.PlayerUuid,
                request.PlayerName,
                request.Snapshot,
                request.BaseRevision,
                request.BaseSha256);
            if (result.Conflict)
            {
                SaveConflict(hosted.WorldId, request.PlayerUuid, request.ProviderId, request.Snapshot, "stale-push");
            }
            await PortableProtocol.WriteJsonAsync(stream, new WaypointSyncReply
            {
                Ok = result.Saved,
                Message = result.Message,
                Revision = result.Revision,
                Sha256 = result.Sha256
            }, _jsonOptions, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Waypoint sync request failed: {ex.Message}");
            try
            {
                await PortableProtocol.WriteJsonAsync(stream, new WaypointSyncReply
                {
                    Ok = false,
                    Message = ex.Message
                }, _jsonOptions, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task SyncLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                bool shouldSync;
                lock (_stateGate)
                {
                    var now = DateTimeOffset.UtcNow;
                    shouldSync = now - _lastSyncUtc >= PeriodicSyncInterval ||
                                 (_scanRequested && now - _lastRelevantChangeUtc >= ChangeDebounce);
                    if (shouldSync) _scanRequested = false;
                }
                if (!shouldSync) continue;
                await FlushAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Waypoint background synchronization failed: {ex.Message}");
            }
        }
    }

    private async Task SyncCoreAsync(CancellationToken token)
    {
        LocalWaypointSession? local;
        HostedWorldSession? hosted;
        HostedWorldSession? pendingHosted;
        RemoteWorldWorkItem[] remotes;
        lock (_stateGate)
        {
            local = _localSession;
            hosted = _hostedSession;
            pendingHosted = _pendingHostedSnapshot;
            _pendingHostedSnapshot = null;
            var cutoff = DateTimeOffset.UtcNow - RemoteSessionTtl;
            foreach (var key in _pendingAnnouncements.Where(pair => pair.Value.LastSeenUtc < cutoff).Select(pair => pair.Key).ToArray())
            {
                _pendingAnnouncements.Remove(key);
            }
            foreach (var pair in _remoteSessions.ToArray())
            {
                var endpointRemoved = false;
                foreach (var address in pair.Value.Addresses.Where(endpoint => endpoint.Value < cutoff).Select(endpoint => endpoint.Key).ToArray())
                {
                    pair.Value.Addresses.Remove(address);
                    endpointRemoved = true;
                }
                if (pair.Value.Addresses.Count == 0)
                {
                    _remoteSessions.Remove(pair.Key);
                }
                else if (endpointRemoved)
                {
                    pair.Value.PullRequested = true;
                    pair.Value.ChangeVersion++;
                }
            }
            remotes = _remoteSessions.Select(pair => new RemoteWorldWorkItem(
                pair.Key,
                pair.Value.WorldId,
                pair.Value.Addresses.Keys.ToArray(),
                pair.Value.Providers.Values.ToArray(),
                pair.Value.PullRequested,
                pair.Value.ChangeVersion)).ToArray();
        }
        if (local is null) return;

        if (pendingHosted is not null)
        {
            SyncHostedCore(local, pendingHosted, local.Identity);
        }
        if (hosted is not null)
        {
            SyncHostedCore(local, hosted, local.Identity);
        }
        foreach (var remote in remotes)
        {
            token.ThrowIfCancellationRequested();
            await SyncRemoteCoreAsync(local, remote, token).ConfigureAwait(false);
        }
        _lastSyncUtc = DateTimeOffset.UtcNow;
        SaveLocalState();
    }

    private void SyncHostedCore(LocalWaypointSession local, HostedWorldSession hosted, LocalIdentityContext identity)
    {
        foreach (var providerInfo in hosted.Providers)
        {
            try
            {
                var context = new WaypointNativeContext(
                    local.GameDirectory,
                    hosted.WorldPath,
                    hosted.WorldId,
                    providerInfo.WorldContextId,
                    providerInfo.ModVersion,
                    IsHost: true,
                    RemoteAddress: null);
                var snapshot = providerInfo.Provider.Export(context);
                var hash = WaypointStoreService.ComputeSnapshotHash(snapshot);
                var key = StateKey(identity.IdentityId, hosted.WorldId, providerInfo.Provider.ProviderId);
                var state = GetOrCreateProviderState(key);
                if (string.Equals(state.LastNativeSha256, hash, StringComparison.OrdinalIgnoreCase)) continue;
                var result = _store.SaveLocalSnapshot(hosted.WorldPath, identity.IdentityId, identity.IdentityName, snapshot);
                state.WorldId = hosted.WorldId;
                state.PlayerUuid = identity.IdentityId;
                state.ProviderId = providerInfo.Provider.ProviderId;
                state.Revision = result.Revision;
                state.Sha256 = result.Sha256;
                state.LastNativeSha256 = hash;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Could not save hosted {providerInfo.Provider.ProviderId} waypoints: {ex.Message}");
            }
        }
    }

    private async Task SyncRemoteCoreAsync(LocalWaypointSession local, RemoteWorldWorkItem remote, CancellationToken token)
    {
        var addresses = remote.Addresses;
        foreach (var providerInfo in remote.Providers)
        {
            var stateKey = StateKey(local.Identity.IdentityId, remote.WorldId, providerInfo.Provider.ProviderId);
            var state = GetOrCreateProviderState(stateKey);
            if (remote.PullRequested || !state.Pulled)
            {
                bool pulled;
                try
                {
                    pulled = await PullRemoteAsync(local, remote, providerInfo, addresses, state, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                {
                    _logger.Warn($"Could not restore remote {providerInfo.Provider.ProviderId} waypoints: {ex.Message}");
                    continue;
                }
                if (!pulled) continue;
            }

            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var context = new WaypointNativeContext(
                        local.GameDirectory,
                        string.Empty,
                        remote.WorldId,
                        providerInfo.WorldContextId,
                        providerInfo.ModVersion,
                        IsHost: false,
                        RemoteAddress: address);
                    var snapshot = providerInfo.Provider.Export(context);
                    var nativeHash = WaypointStoreService.ComputeSnapshotHash(snapshot);
                    if (string.Equals(nativeHash, state.LastNativeSha256, StringComparison.OrdinalIgnoreCase)) continue;
                    var pushed = await PushRemoteAsync(local, remote, providerInfo, addresses, state, snapshot, token).ConfigureAwait(false);
                    if (pushed) break;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Could not inspect local {providerInfo.Provider.ProviderId} waypoints: {ex.Message}");
                }
            }
        }
        lock (_stateGate)
        {
            if (_remoteSessions.TryGetValue(remote.Key, out var current) &&
                current.ChangeVersion == remote.ChangeVersion)
            {
                current.PullRequested = false;
            }
        }
    }

    private async Task<bool> PullRemoteAsync(
        LocalWaypointSession local,
        RemoteWorldWorkItem remote,
        RemoteProvider providerInfo,
        IReadOnlyList<string> addresses,
        WaypointLocalProviderState state,
        CancellationToken token)
    {
        var request = new WaypointSyncEnvelope
        {
            MessageType = PullMessageType,
            WorldId = remote.WorldId,
            PlayerUuid = local.Identity.IdentityId,
            PlayerName = local.Identity.IdentityName,
            ProviderId = providerInfo.Provider.ProviderId,
            WorldContextId = providerInfo.WorldContextId
        };
        var response = await SendRequestAsync(addresses, request, token).ConfigureAwait(false);
        if (response is null || !response.Ok) return false;

        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (response.Snapshot is not null)
        {
            providerInfo.Provider.Validate(response.Snapshot);
            if (!string.Equals(response.Snapshot.WorldContextId, providerInfo.WorldContextId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Remote waypoint snapshot belongs to a different world context.");
            }
            foreach (var address in addresses)
            {
                var context = new WaypointNativeContext(
                    local.GameDirectory,
                    string.Empty,
                    remote.WorldId,
                    providerInfo.WorldContextId,
                    providerInfo.ModVersion,
                    IsHost: false,
                    RemoteAddress: address);
                var result = providerInfo.Provider.Import(context, response.Snapshot, []);
                managed.UnionWith(result.ManagedRelativePaths);
            }
            WaypointPath.DeleteRemovedManagedFiles(
                local.GameDirectory,
                state.ManagedRelativePaths,
                managed);
            state.LastNativeSha256 = WaypointStoreService.ComputeSnapshotHash(response.Snapshot);
        }
        else
        {
            state.LastNativeSha256 = string.Empty;
        }
        state.WorldId = remote.WorldId;
        state.PlayerUuid = local.Identity.IdentityId;
        state.ProviderId = providerInfo.Provider.ProviderId;
        state.Revision = response.Revision;
        state.Sha256 = response.Sha256;
        state.Pulled = true;
        state.ManagedRelativePaths = managed.ToList();
        return true;
    }

    private async Task<bool> PushRemoteAsync(
        LocalWaypointSession local,
        RemoteWorldWorkItem remote,
        RemoteProvider providerInfo,
        IReadOnlyList<string> addresses,
        WaypointLocalProviderState state,
        WaypointSnapshot snapshot,
        CancellationToken token)
    {
        var request = new WaypointSyncEnvelope
        {
            MessageType = PushMessageType,
            WorldId = remote.WorldId,
            PlayerUuid = local.Identity.IdentityId,
            PlayerName = local.Identity.IdentityName,
            ProviderId = providerInfo.Provider.ProviderId,
            WorldContextId = providerInfo.WorldContextId,
            BaseRevision = state.Revision,
            BaseSha256 = state.Sha256,
            Snapshot = snapshot
        };
        var response = await SendRequestAsync(addresses, request, token).ConfigureAwait(false);
        if (response is null) return false;
        if (!response.Ok)
        {
            SaveConflict(remote.WorldId, local.Identity.IdentityId, providerInfo.Provider.ProviderId, snapshot, "remote-rejected");
            state.Pulled = false;
            RequestRemotePull(remote.Key);
            return false;
        }
        state.Revision = response.Revision;
        state.Sha256 = response.Sha256;
        state.LastNativeSha256 = WaypointStoreService.ComputeSnapshotHash(snapshot);
        return true;
    }

    private async Task<WaypointSyncReply?> SendRequestAsync(
        IReadOnlyList<string> addresses,
        WaypointSyncEnvelope request,
        CancellationToken token)
    {
        foreach (var address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(address, out var ip)) continue;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdownCts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var client = new TcpClient(ip.AddressFamily);
                await client.ConnectAsync(ip, WorldTransferService.TransferPort, timeoutCts.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                await PortableProtocol.WriteJsonAsync(stream, request, _jsonOptions, timeoutCts.Token).ConfigureAwait(false);
                var frame = await PortableProtocol.ReadFrameAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                var response = PortableProtocol.Deserialize<WaypointSyncReply>(frame, _jsonOptions);
                if (response is not null && response.Protocol == ProtocolName && response.ProtocolVersion == ProtocolVersion)
                {
                    return response;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or InvalidDataException)
            {
                token.ThrowIfCancellationRequested();
                _logger.Warn($"Waypoint synchronization route {address} failed: {ex.Message}");
            }
        }
        return null;
    }

    private void ImportSnapshot(
        IWaypointProvider provider,
        WaypointNativeContext context,
        string playerUuid,
        WaypointStoredSnapshot stored)
    {
        var key = StateKey(playerUuid, context.WorldId, provider.ProviderId);
        var state = GetOrCreateProviderState(key);
        var result = provider.Import(context, stored.Snapshot, state.ManagedRelativePaths);
        state.WorldId = context.WorldId;
        state.PlayerUuid = playerUuid;
        state.ProviderId = provider.ProviderId;
        state.Revision = stored.Revision;
        state.Sha256 = stored.Sha256;
        state.LastNativeSha256 = WaypointStoreService.ComputeSnapshotHash(stored.Snapshot);
        state.ManagedRelativePaths = result.ManagedRelativePaths.ToList();
        state.Pulled = true;
    }

    private void ConfigureWatchers(LocalWaypointSession session)
    {
        lock (_stateGate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            foreach (var root in session.Providers.SelectMany(item => item.Provider.GetWatchRoots(session.GameDirectory))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(root);
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnNativeFileChanged;
                watcher.Created += OnNativeFileChanged;
                watcher.Deleted += OnNativeFileChanged;
                watcher.Renamed += OnNativeFileChanged;
                _watchers.Add(watcher);
            }
        }
    }

    private void OnNativeFileChanged(object sender, FileSystemEventArgs args)
    {
        var fileName = Path.GetFileName(args.FullPath);
        if (!fileName.Equals("waypoints.json", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        lock (_stateGate)
        {
            _lastRelevantChangeUtc = DateTimeOffset.UtcNow;
            _scanRequested = true;
        }
    }

    private void RequestScan()
    {
        lock (_stateGate)
        {
            _lastRelevantChangeUtc = DateTimeOffset.UtcNow - ChangeDebounce;
            _scanRequested = true;
        }
    }

    private WaypointLocalProviderState GetOrCreateProviderState(string key)
    {
        lock (_stateGate)
        {
            if (!_localState.Providers.TryGetValue(key, out var state))
            {
                state = new WaypointLocalProviderState();
                _localState.Providers[key] = state;
            }
            return state;
        }
    }

    private WaypointLocalState LoadLocalState()
    {
        var path = GetLocalStatePath();
        try
        {
            if (!File.Exists(path)) return new WaypointLocalState();
            var state = JsonSerializer.Deserialize<WaypointLocalState>(File.ReadAllText(path), _jsonOptions);
            if (state?.SchemaVersion != 1) return new WaypointLocalState();
            state.Providers = new Dictionary<string, WaypointLocalProviderState>(state.Providers, StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Waypoint local state could not be read and will be rebuilt: {ex.Message}");
            return new WaypointLocalState();
        }
    }

    private void SaveLocalState()
    {
        lock (_stateGate)
        {
            _localState.UpdatedAtUtc = DateTimeOffset.UtcNow;
            AtomicFile.WriteAllText(GetLocalStatePath(), JsonSerializer.Serialize(_localState, _jsonOptions));
        }
    }

    private void SaveConflict(
        string worldId,
        string playerUuid,
        string providerId,
        WaypointSnapshot snapshot,
        string reason)
    {
        try
        {
            var safeReason = string.Concat(reason.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
            var directory = Path.Combine(_paths.WaypointConflicts, worldId, playerUuid, providerId);
            _paths.EnsureUnderRoot(directory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{safeReason}.json");
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot, _jsonOptions));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not preserve conflicting waypoint snapshot: {ex.Message}");
        }
    }

    private string GetLocalStatePath() => _paths.WaypointSyncStateFile;

    private void MigrateLegacyState()
    {
        var legacyRoot = Path.Combine(_paths.Personal, "WaypointSync");
        if (!Directory.Exists(legacyRoot)) return;
        try
        {
            var legacyState = Path.Combine(legacyRoot, "state.json");
            if (File.Exists(legacyState))
            {
                if (!File.Exists(_paths.WaypointSyncStateFile))
                {
                    File.Move(legacyState, _paths.WaypointSyncStateFile);
                }
                else
                {
                    File.Delete(legacyState);
                }
            }

            var legacyConflicts = Path.Combine(legacyRoot, "Conflicts");
            if (Directory.Exists(legacyConflicts))
            {
                foreach (var source in Directory.EnumerateFiles(legacyConflicts, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(legacyConflicts, source);
                    var destination = Path.Combine(_paths.WaypointConflicts, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination))
                    {
                        destination += $".legacy-{Guid.NewGuid():N}";
                    }
                    File.Move(source, destination);
                }
            }

            if (!Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(legacyRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Legacy waypoint state could not be flattened: {ex.Message}");
        }
    }

    private string[] EnumerateWorlds()
    {
        if (!Directory.Exists(_paths.Worlds)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(_paths.Worlds, "*", SearchOption.TopDirectoryOnly)
            .Where(WorldTransferService.IsMinecraftWorldDirectory)
            .ToArray();
    }

    private HostedWorldSession CreateHostedSession(
        LocalWaypointSession local,
        string worldPath,
        LocalIdentityContext identity,
        string packHash)
    {
        var worldId = _worldMetadata.EnsureWorldId(worldPath, CreateMetadataContext(local, identity, packHash));
        var providers = local.Providers
            .Select(item => new HostedProvider(
                item.Provider,
                item.ModVersion,
                item.Provider.ReadWorldContextId(worldPath) ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.WorldContextId))
            .ToArray();
        _store.EnsureManifest(worldPath);
        return new HostedWorldSession(worldPath, worldId, providers);
    }

    private LocalWaypointSession? ResolveLocalSessionForWorld(
        string worldPath,
        LocalIdentityContext identity,
        LocalWaypointSession? current)
    {
        var metadata = _worldMetadata.Read(worldPath);
        var packRelativePath = metadata?.BuildRelativePath;
        if (string.IsNullOrWhiteSpace(packRelativePath)) return current;
        if (current is not null &&
            string.Equals(current.PackRelativePath, packRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        try
        {
            var packDirectory = _paths.CombineUnderPacks(packRelativePath);
            if (!Directory.Exists(packDirectory))
            {
                _logger.Warn($"Waypoints were not refreshed before transfer because pack '{packRelativePath}' is missing.");
                return null;
            }
            var gameDirectory = _paths.CombineUnderInstances(packRelativePath);
            if (!Directory.Exists(gameDirectory))
            {
                _logger.Warn("Waypoints were not refreshed before transfer because the writable game instance is missing.");
                return null;
            }
            return new LocalWaypointSession(
                packRelativePath,
                gameDirectory,
                identity,
                _providerRegistry.Detect(packDirectory));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.Warn($"Waypoints were not refreshed before transfer: {ex.Message}");
            return null;
        }
    }

    private void QueueHostedSnapshotAndClear()
    {
        lock (_stateGate)
        {
            if (_hostedSession is null) return;
            _pendingHostedSnapshot = _hostedSession;
            _hostedSession = null;
            _scanRequested = true;
            _lastRelevantChangeUtc = DateTimeOffset.UtcNow - ChangeDebounce;
        }
    }

    private void RequestRemotePull(string key)
    {
        lock (_stateGate)
        {
            if (!_remoteSessions.TryGetValue(key, out var remote)) return;
            remote.PullRequested = true;
            remote.ChangeVersion++;
            _scanRequested = true;
            _lastRelevantChangeUtc = DateTimeOffset.UtcNow - ChangeDebounce;
        }
    }

    private static string StateKey(string playerUuid, string worldId, string providerId) =>
        $"{playerUuid.ToLowerInvariant()}|{worldId.ToLowerInvariant()}|{providerId.ToLowerInvariant()}";

    private static WorldMetadataContext CreateMetadataContext(
        LocalWaypointSession local,
        LocalIdentityContext identity,
        string packHash) => new()
    {
        BuildName = Path.GetFileName(local.PackRelativePath),
        BuildRelativePath = local.PackRelativePath,
        PackHash = packHash,
        OwnerIdentityId = identity.IdentityId,
        OwnerIdentityName = identity.IdentityName
    };

    private static void ValidateRequest(WaypointSyncEnvelope request)
    {
        if (!string.Equals(request.Protocol, ProtocolName, StringComparison.Ordinal) ||
            request.ProtocolVersion != ProtocolVersion ||
            !Guid.TryParse(request.WorldId, out var worldId) || worldId == Guid.Empty ||
            !Guid.TryParse(request.PlayerUuid, out var playerId) || playerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ProviderId) ||
            string.IsNullOrWhiteSpace(request.WorldContextId))
        {
            throw new InvalidDataException("Waypoint sync request has an incompatible or incomplete envelope.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FlushAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }
        _shutdownCts.Cancel();
        try
        {
            await _syncLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        lock (_stateGate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
        _shutdownCts.Dispose();
        _syncGate.Dispose();
    }

    private sealed record LocalWaypointSession(
        string PackRelativePath,
        string GameDirectory,
        LocalIdentityContext Identity,
        IReadOnlyList<(IWaypointProvider Provider, string ModVersion)> Providers);

    private sealed record HostedProvider(IWaypointProvider Provider, string ModVersion, string WorldContextId);
    private sealed record HostedWorldSession(string WorldPath, string WorldId, IReadOnlyList<HostedProvider> Providers);
    private sealed record RemoteProvider(IWaypointProvider Provider, string ModVersion, string WorldContextId);
    private sealed record PendingHostAnnouncement(
        string WorldId,
        string HostIdentityId,
        string Address,
        IReadOnlyList<WaypointProviderAnnouncement> Providers,
        DateTimeOffset LastSeenUtc);
    private sealed record RemoteWorldWorkItem(
        string Key,
        string WorldId,
        IReadOnlyList<string> Addresses,
        IReadOnlyList<RemoteProvider> Providers,
        bool PullRequested,
        long ChangeVersion);

    private sealed class RemoteWorldSession
    {
        public RemoteWorldSession(string worldId)
        {
            WorldId = worldId;
        }

        public string WorldId { get; }
        public Dictionary<string, DateTimeOffset> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RemoteProvider> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool PullRequested { get; set; }
        public long ChangeVersion { get; set; }
    }
}

public sealed record WaypointHostAdvertisement(
    string WorldId,
    IReadOnlyList<WaypointProviderAnnouncement> Providers);

public sealed class WaypointLocalState
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, WaypointLocalProviderState> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WaypointLocalProviderState
{
    public string WorldId { get; set; } = "";
    public string PlayerUuid { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public long Revision { get; set; }
    public string Sha256 { get; set; } = "";
    public string LastNativeSha256 { get; set; } = "";
    public bool Pulled { get; set; }
    public List<string> ManagedRelativePaths { get; set; } = [];
}
