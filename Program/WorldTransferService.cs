using System.Buffers.Binary;
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
    private const string TransferProtocol = "MinecraftPortableWorldV2";
    private const string PingProtocol = "MinecraftPortablePingV1";

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly MinecraftProcessService _minecraft;
    private readonly SettingsService _settingsService;
    private readonly WorldMetadataService _worldMetadata;
    private readonly LocalIdentityService _identityService;
    private readonly PortableIdentityAdapterService _identityAdapter;
    private readonly WorldPlayerProfileService _playerProfiles;
    private readonly WorldTransferRuntimeOptions _runtimeOptions;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonSerializerOptions _indentedJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    public WorldTransferService(
        AppPaths paths,
        Logger logger,
        MinecraftProcessService minecraft,
        SettingsService settingsService,
        WorldMetadataService worldMetadata,
        LocalIdentityService identityService,
        PortableIdentityAdapterService identityAdapter,
        WorldPlayerProfileService playerProfiles,
        WorldTransferRuntimeOptions? runtimeOptions = null)
    {
        _paths = paths;
        _logger = logger;
        _minecraft = minecraft;
        _settingsService = settingsService;
        _worldMetadata = worldMetadata;
        _identityService = identityService;
        _identityAdapter = identityAdapter;
        _playerProfiles = playerProfiles;
        _runtimeOptions = runtimeOptions ?? new WorldTransferRuntimeOptions();
        _runtimeOptions.Validate();
        WorldTransferRecoveryService.Recover(paths, logger);
    }

    public event Action<string>? StatusChanged;
    public event Action? BecameHost;
    public event Action<long, long>? ProgressChanged;

    public void StartListener(AppSettings settings)
    {
        StopListener();
        _listenerCts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenAsync(settings, _listenerCts.Token));
        _logger.Info("World transfer listener started.");
    }

    public void StopListener()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;
    }

    public async Task SendWorldAsync(PeerViewModel peer, AppSettings settings, string worldPath, CancellationToken token)
    {
        if (_minecraft.IsClientRunning)
        {
            throw new InvalidOperationException("Close Minecraft before transferring a world.");
        }
        RaiseProgress(0, 0);
        var identity = ResolveIdentityContext(settings);
        if (peer.PackStatus != "OK")
        {
            _logger.Warn($"Pack hash mismatch ({peer.PackStatus}); world transfer is allowed by local settings.");
        }

        await VerifyPeerTransferReadyAsync(peer, settings, token);

        var worldDir = ResolveWorldToSend(worldPath);
        WorldAccessGuard.EnsureClosed(worldDir);
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
                TransferId = transferId,
                SenderName = identity.IdentityName,
                SenderIdentityId = identity.IdentityId,
                SenderIdentityName = identity.IdentityName,
                OwnerIdentityId = ownerId.id,
                OwnerIdentityName = ownerId.name,
                Size = fileInfo.Length,
                WorldSha256 = worldSha,
                PlayerManifestSha256 = playerManifestSha,
                FileName = Path.GetFileName(archivePath),
                WorldName = worldName
            };

            StatusChanged?.Invoke("Sending world archive...");
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(peer.VpnIp), _runtimeOptions.Port, token);
            await using var stream = client.GetStream();
            await WriteJsonAsync(stream, header, token);
            await using (var file = File.OpenRead(archivePath))
            {
                await CopyWithProgressAsync(file, stream, fileInfo.Length, progress =>
                {
                    RaiseProgress(progress, fileInfo.Length);
                }, token);
            }

            var ready = await ReadJsonAsync<WorldTransferAck>(stream, token);
            if (ready is null || !ready.Ok || ready.Stage != "Ready" || ready.TransferId != transferId ||
                !string.Equals(ready.WorldSha256, worldSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ready.PlayerManifestSha256, playerManifestSha, StringComparison.OrdinalIgnoreCase))
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
                TransferId = transferId,
                Command = "Commit"
            }, token);

            var committed = await ReadJsonAsync<WorldTransferAck>(stream, token);
            if (committed is null || !committed.Ok || committed.Stage != "Committed" || committed.TransferId != transferId ||
                !string.Equals(committed.WorldSha256, worldSha, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(committed.PlayerManifestSha256))
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
            RaiseProgress(0, 0);
            if (completed || journal.State != "CommitSent") DeleteDirectoryIfExists(transactionRoot);
        }
    }

    private async Task ListenAsync(AppSettings settings, CancellationToken token)
    {
        var listener = new TcpListener(_runtimeOptions.ListenAddress, _runtimeOptions.Port);
        listener.Start();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => ReceiveWorldAsync(client, settings, token), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"World transfer listener failed: {ex.Message}");
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ReceiveWorldAsync(TcpClient client, AppSettings settings, CancellationToken token)
    {
        using var clientScope = client;
        await using var stream = client.GetStream();
        var identity = ResolveIdentityContext(settings);
        WorldTransferHeader? header = null;
        string? transactionRoot = null;
        string? receivedPath = null;
        string? tempWorldPath = null;
        WorldTransferJournal? journal = null;
        try
        {
            header = await ReadJsonAsync<WorldTransferHeader>(stream, token)
                ?? throw new InvalidOperationException("Invalid transfer header.");
            if (header.Protocol == PingProtocol)
            {
                await WriteJsonAsync(stream, new WorldTransferAck { Ok = true, Stage = "Ping", Message = "ready" }, token);
                return;
            }
            if (header.Protocol != TransferProtocol || !Guid.TryParseExact(header.TransferId, "N", out var parsedTransferId))
            {
                throw new InvalidOperationException("The sender uses an incompatible world transfer protocol.");
            }
            if (_minecraft.IsClientRunning)
            {
                throw new InvalidOperationException("Close Minecraft before receiving a world.");
            }
            if (header.Size <= 0 || header.Size > settings.MaxArchiveBytes ||
                string.IsNullOrWhiteSpace(header.WorldSha256) ||
                string.IsNullOrWhiteSpace(header.PlayerManifestSha256))
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
            RaiseProgress(0, header.Size);
            await using (var file = new FileStream(receivedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await CopyExactlyWithLimitWithProgressAsync(stream, file, header.Size, settings.MaxArchiveBytes, progress =>
                {
                    RaiseProgress(progress, header.Size);
                }, token);
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

            journal.State = "Ready";
            WriteJournal(transactionRoot, journal);
            await WriteJsonAsync(stream, new WorldTransferAck
            {
                Ok = true,
                Stage = "Ready",
                TransferId = header.TransferId,
                Message = "ready",
                WorldSha256 = receivedWorldSha,
                PlayerManifestSha256 = sourceManifestSha
            }, token);
            var control = await ReadJsonAsync<WorldTransferControl>(stream, token);
            if (control is null || control.Protocol != TransferProtocol ||
                control.TransferId != header.TransferId || control.Command != "Commit")
            {
                throw new InvalidOperationException("World transfer commit command is invalid.");
            }

            journal.State = "CommitReceived";
            WriteJournal(transactionRoot, journal);
            var installedWorldPath = InstallReceivedWorld(tempWorldPath, header.WorldName);
            tempWorldPath = null;
            journal.State = "Installed";
            journal.InstalledWorldPath = installedWorldPath;
            WriteJournal(transactionRoot, journal);
            await WriteJsonAsync(stream, new WorldTransferAck
            {
                Ok = true,
                Stage = "Committed",
                TransferId = header.TransferId,
                Message = "accepted",
                WorldSha256 = receivedWorldSha,
                PlayerManifestSha256 = installedManifestSha
            }, token);
            journal.State = "Committed";
            WriteJournal(transactionRoot, journal);
            settings.SelectedWorldRelativePath = Path.GetRelativePath(_paths.Worlds, installedWorldPath);
            _settingsService.Save(settings);
            BecameHost?.Invoke();
            if (_runtimeOptions.StartClientAfterReceive)
            {
                await _minecraft.StartClientAsync(settings, null, 0, runtimeProgress: null, token);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"World receive failed: {ex.Message}");
            try
            {
                await WriteJsonAsync(stream, new WorldTransferAck
                {
                    Ok = false,
                    Stage = "Rejected",
                    TransferId = header?.TransferId ?? string.Empty,
                    Message = ex.Message,
                    WorldSha256 = header?.WorldSha256 ?? string.Empty
                }, CancellationToken.None);
            }
            catch
            {
            }
        }
        finally
        {
            RaiseProgress(0, 0);
            DeleteFileIfExists(receivedPath);
            DeleteDirectoryIfExists(tempWorldPath);
            if (transactionRoot is not null) DeleteDirectoryIfExists(transactionRoot);
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

    private async Task VerifyPeerTransferReadyAsync(PeerViewModel peer, AppSettings settings, CancellationToken token)
    {
        var identity = _identityService.ResolveContext(settings);
        if (string.IsNullOrWhiteSpace(peer.VpnIp) || !IPAddress.TryParse(peer.VpnIp, out var ip))
        {
            throw new InvalidOperationException("Selected player does not have a valid network IP address.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, _runtimeOptions.Port, timeoutCts.Token);
            await using var stream = client.GetStream();
            await WriteJsonAsync(stream, new WorldTransferHeader
            {
                Protocol = PingProtocol,
                SenderName = identity.IdentityName,
                SenderIdentityId = identity.IdentityId,
                SenderIdentityName = identity.IdentityName,
                Size = 0,
                FileName = "ping",
                WorldName = ""
            }, timeoutCts.Token);

            var ack = await ReadJsonAsync<WorldTransferAck>(stream, timeoutCts.Token);
            if (ack is null || !ack.Ok)
            {
                throw new InvalidOperationException(ack?.Message ?? "receiver did not accept transfer ping");
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException)
        {
            throw new InvalidOperationException(BuildPeerConnectionMessage(peer.VpnIp), ex);
        }
    }

    private void RaiseProgress(long current, long total)
    {
        try
        {
            ProgressChanged?.Invoke(current, total);
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
        var identity = _identityService.ResolveContext(settings);
        var descriptor = PackManifestService.Load(_paths.CombineUnderPacks(settings.ClientRelativePath));
        identity.MinecraftUuid = _identityAdapter.ResolveMinecraftUuid(identity, descriptor).ToString("D");
        return identity;
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
        CancellationToken token)
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
            progress(total);
        }
    }

    private static async Task CopyWithProgressAsync(
        Stream input,
        Stream output,
        long totalSize,
        Action<long> progress,
        CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, totalSize - total)), token);
            if (read <= 0) break;
            total += read;
            if (total > totalSize) throw new InvalidOperationException("Transfer size exceeds expected archive size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            progress(total);
        }

        if (total != totalSize)
        {
            throw new InvalidOperationException("Transfer data size mismatch.");
        }
    }

    private async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, _jsonOptions));
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private async Task<T?> ReadJsonAsync<T>(Stream stream, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length <= 0 || length > 1024 * 1024) throw new InvalidOperationException("Invalid JSON frame length.");
        var bytes = new byte[length];
        await ReadExactAsync(stream, bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
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
        StopListener();
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask;
            }
            catch
            {
            }
        }
    }
}

public sealed class WorldTransferRuntimeOptions
{
    public int Port { get; init; } = WorldTransferService.TransferPort;
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;
    public bool StartClientAfterReceive { get; init; } = true;

    internal void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "World transfer port must be between 1 and 65535.");
        }
        ArgumentNullException.ThrowIfNull(ListenAddress);
    }
}
