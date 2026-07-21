using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace Minecraft;

public sealed class MinecraftProcessService
{
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly LocalIdentityService _identityService;
    private readonly PortableIdentityAdapterService _identityAdapter;
    private readonly WorldPlayerProfileService _playerProfiles;
    private readonly PackInstanceService _packInstances;
    private readonly PackRuntimeService _packRuntimes;
    private readonly WaypointSyncService _waypointSync;
    private readonly SkinService _skinService;
    private readonly MinecraftWindowPlacementService _gameWindowPlacement;
    private readonly ConcurrentDictionary<int, byte> _activeClientProcesses = new();
    private int _clientPreparing;
    private int _tcpOwnershipWarningLogged;

    public bool IsClientRunning => !_activeClientProcesses.IsEmpty;
    public bool IsClientPreparing => Volatile.Read(ref _clientPreparing) != 0;
    public event Action<bool>? ClientRunningChanged;
    public event Action<bool>? ClientPreparingChanged;

    public bool OwnsTcpListener(int port)
    {
        if (port is <= 0 or > 65535 || _activeClientProcesses.IsEmpty) return false;
        var processIds = _activeClientProcesses.Keys.ToHashSet();
        if (!OperatingSystem.IsWindows())
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(listener => listener.Port == port);
        }

        try
        {
            return IsTcp4ListenerOwnedBy(port, processIds) ||
                   IsTcp6ListenerOwnedBy(port, processIds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            if (Interlocked.Exchange(ref _tcpOwnershipWarningLogged, 1) == 0)
            {
                _logger.Warn($"Could not verify Minecraft LAN listener ownership: {ex.Message}");
            }
            return false;
        }
    }

    public MinecraftProcessService(
        AppPaths paths,
        Logger logger,
        LocalIdentityService identityService,
        PortableIdentityAdapterService identityAdapter,
        WorldPlayerProfileService playerProfiles,
        PackInstanceService packInstances,
        PackRuntimeService packRuntimes,
        WaypointSyncService waypointSync,
        SkinService skinService)
    {
        _paths = paths;
        _logger = logger;
        _identityService = identityService;
        _identityAdapter = identityAdapter;
        _playerProfiles = playerProfiles;
        _packInstances = packInstances;
        _packRuntimes = packRuntimes;
        _waypointSync = waypointSync;
        _skinService = skinService;
        _gameWindowPlacement = new MinecraftWindowPlacementService(paths, logger);
    }

    public async Task StartClientAsync(
        AppSettings settings,
        string? targetHost,
        int targetPort,
        IProgress<RuntimePreparationProgress>? runtimeProgress = null,
        CancellationToken token = default)
    {
        if (IsClientRunning)
        {
            throw new InvalidOperationException("Minecraft is already running from this application.");
        }
        if (Interlocked.CompareExchange(ref _clientPreparing, 1, 0) != 0)
        {
            throw new InvalidOperationException("Minecraft is already being prepared.");
        }

        NotifyClientPreparingChanged(true);
        try
        {
            await StartClientCoreAsync(settings, targetHost, targetPort, runtimeProgress, token).ConfigureAwait(false);
        }
        finally
        {
            _packRuntimes.CleanupLaunchTemporaryFiles();
            Interlocked.Exchange(ref _clientPreparing, 0);
            NotifyClientPreparingChanged(false);
        }
    }

    private async Task StartClientCoreAsync(
        AppSettings settings,
        string? targetHost,
        int targetPort,
        IProgress<RuntimePreparationProgress>? runtimeProgress,
        CancellationToken token)
    {
        if (IsClientRunning)
        {
            throw new InvalidOperationException("Minecraft is already running from this application.");
        }
        var packDir = _paths.CombineUnderPacks(settings.ClientRelativePath);
        if (!HasPackData(packDir))
        {
            throw new DirectoryNotFoundException($"Minecraft pack folder has no {PackManifestService.ManifestFileName}: {packDir}");
        }

        var descriptor = PackManifestService.Load(packDir);
        var identityContext = _identityService.ResolveContext(settings);
        var runtime = await _packRuntimes.PrepareAsync(settings.ClientRelativePath, runtimeProgress, token);
        if (!string.Equals(runtime.Descriptor.DescriptorHash, descriptor.DescriptorHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pack manifest changed while its runtime was being prepared. Start the game again.");
        }
        var identityJvmArguments = await _identityAdapter.PrepareJvmArgumentsAsync(runtime, token);
        var skinRegistryPath = _skinService.PrepareRegistry(settings, identityContext);

        var instance = await _packInstances.PrepareAsync(settings.ClientRelativePath, token);
        var gameDir = instance.GameDirectory;
        EnsureWorldsDirectoryAndSavesLink(gameDir);
        ValidatePackCompatibility(packDir);
        EnsureModernFixShutdownWorkaround(gameDir);
        _playerProfiles.PrepareWorldsForLaunch(_paths.Worlds, identityContext);
        await _waypointSync.PrepareForLaunchAsync(settings.ClientRelativePath, identityContext, token).ConfigureAwait(false);

        var launcher = _packRuntimes.CreateLocalLauncher(runtime);
        var profile = await launcher.GetVersionAsync(runtime.ProfileId, token);
        var runtimePath = launcher.MinecraftPath;
        var launchPath = new MinecraftPath(gameDir)
        {
            Library = runtimePath.Library,
            Versions = runtimePath.Versions,
            Assets = runtimePath.Assets,
            Resource = runtimePath.Resource,
            Runtime = runtimePath.Runtime
        };
        launchPath.CreateDirs();

        var javaTempDir = Path.Combine(_paths.Personal, "Temp", "Java", settings.ClientRelativePath);
        _paths.EnsureUnderRoot(javaTempDir);
        Directory.CreateDirectory(javaTempDir);
        var session = MSession.CreateOfflineSession(identityContext.IdentityName);
        session.UUID = identityContext.MinecraftUuid;
        session.AccessToken = identityContext.SessionAccessToken;
        session.UserType = "mojang";
        session.Xuid = "";
        var maximumRamMb = checked(settings.MaxMemoryGb * 1024);
        var extraJvmArguments = new List<MArgument>
        {
            new("-Dfile.encoding=UTF-8"),
            new("-Djava.net.preferIPv4Stack=true"),
            new("-Djava.net.preferIPv6Addresses=false"),
            new($"-Djava.io.tmpdir={javaTempDir}"),
            new($"-Dminecraft.portable.skin.registry={skinRegistryPath}")
        };
        extraJvmArguments.AddRange(identityJvmArguments.Select(argument => new MArgument(argument)));
        var launchOption = new MLaunchOption
        {
            Path = launchPath,
            JavaPath = runtime.JavaPath,
            Session = session,
            MaximumRamMb = maximumRamMb,
            MinimumRamMb = Math.Min(2048, maximumRamMb),
            GameLauncherName = "Minecraft Portable",
            GameLauncherVersion = "1",
            VersionType = $"{descriptor.Loader.Type} {descriptor.Loader.Version}".Trim(),
            FullScreen = false,
            ExtraJvmArguments = extraJvmArguments
        };
        if (!string.IsNullOrWhiteSpace(targetHost))
        {
            launchOption.ServerIp = targetHost;
            launchOption.ServerPort = Math.Clamp(targetPort, 1, 65535);
        }

        using var minecraftProcess = launcher.BuildProcess(profile, launchOption);
        minecraftProcess.StartInfo.WorkingDirectory = gameDir;
        minecraftProcess.StartInfo.UseShellExecute = false;
        minecraftProcess.StartInfo.CreateNoWindow = true;
        minecraftProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
        minecraftProcess.StartInfo.Environment["TEMP"] = javaTempDir;
        minecraftProcess.StartInfo.Environment["TMP"] = javaTempDir;
        if (!minecraftProcess.Start())
        {
            throw new InvalidOperationException("Minecraft process could not be started.");
        }

        var processId = minecraftProcess.Id;
        if (_activeClientProcesses.TryAdd(processId, 0) && _activeClientProcesses.Count == 1)
        {
            NotifyClientRunningChanged(true);
        }
        _ = MonitorClientExitAsync(processId, settings.ClientRelativePath);

        await Task.Delay(TimeSpan.FromSeconds(2), token);
        if (minecraftProcess.HasExited && minecraftProcess.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Minecraft exited during startup with code {minecraftProcess.ExitCode}." + ReadLatestLogTail(gameDir));
        }

        _logger.Info(string.IsNullOrWhiteSpace(targetHost)
            ? $"Minecraft client started with profile {runtime.ProfileId}."
            : $"Minecraft client started for {targetHost}:{targetPort} with profile {runtime.ProfileId}.");
    }

    private async Task MonitorClientExitAsync(int processId, string packRelativePath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var placementCancellation = new CancellationTokenSource();
            var placementTask = _gameWindowPlacement.TrackAsync(processId, placementCancellation.Token);
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            finally
            {
                placementCancellation.Cancel();
                await placementTask.ConfigureAwait(false);
            }
            await _waypointSync.FlushAsync().ConfigureAwait(false);
            await _packInstances.CleanupGeneratedLocalArtifactsAsync(packRelativePath, process.ExitCode == 0).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await _waypointSync.FlushAsync().ConfigureAwait(false);
            await _packInstances.CleanupGeneratedLocalArtifactsAsync(packRelativePath, removeSessionLogs: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not clean generated local instance data after Minecraft exited: {ex.Message}");
        }
        finally
        {
            CleanupJavaTemporaryDirectory(packRelativePath);
            if (_activeClientProcesses.TryRemove(processId, out _) && _activeClientProcesses.IsEmpty)
            {
                NotifyClientRunningChanged(false);
            }
        }
    }

    private void CleanupJavaTemporaryDirectory(string packRelativePath)
    {
        try
        {
            var tempRoot = Path.GetFullPath(Path.Combine(_paths.Personal, "Temp"));
            var javaRoot = Path.GetFullPath(Path.Combine(tempRoot, "Java"));
            var target = Path.GetFullPath(Path.Combine(javaRoot, packRelativePath));
            if (!target.StartsWith(javaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            TryDeleteDirectoryIfEmpty(javaRoot);
            TryDeleteDirectoryIfEmpty(tempRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Minecraft temporary files could not be removed: {ex.Message}");
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
    }

    private static bool IsTcp4ListenerOwnedBy(int port, HashSet<int> processIds)
    {
        var size = 0;
        var status = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            AddressFamilyInterNetwork,
            TcpTableClass.OwnerPidListener,
            0);
        if (status is not (ErrorInsufficientBuffer or ErrorSuccess) || size <= sizeof(int))
        {
            throw new InvalidOperationException($"GetExtendedTcpTable sizing failed with status {status}.");
        }

        var table = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(
                table,
                ref size,
                order: false,
                AddressFamilyInterNetwork,
                TcpTableClass.OwnerPidListener,
                0);
            if (status != ErrorSuccess)
            {
                throw new InvalidOperationException($"GetExtendedTcpTable failed with status {status}.");
            }

            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowAddress = IntPtr.Add(table, sizeof(int));
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowAddress);
                var listenerPort = unchecked((ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort));
                if (listenerPort == port && processIds.Contains(unchecked((int)row.OwningProcessId)))
                {
                    return true;
                }
                rowAddress = IntPtr.Add(rowAddress, rowSize);
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static bool IsTcp6ListenerOwnedBy(int port, HashSet<int> processIds)
    {
        var size = 0;
        var status = GetExtendedTcpTable(
            IntPtr.Zero,
            ref size,
            order: false,
            AddressFamilyInterNetworkV6,
            TcpTableClass.OwnerPidListener,
            0);
        if (status is not (ErrorInsufficientBuffer or ErrorSuccess) || size <= sizeof(int))
        {
            throw new InvalidOperationException($"GetExtendedTcpTable IPv6 sizing failed with status {status}.");
        }

        var table = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(
                table,
                ref size,
                order: false,
                AddressFamilyInterNetworkV6,
                TcpTableClass.OwnerPidListener,
                0);
            if (status != ErrorSuccess)
            {
                throw new InvalidOperationException($"GetExtendedTcpTable IPv6 failed with status {status}.");
            }

            var count = Marshal.ReadInt32(table);
            var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            var rowAddress = IntPtr.Add(table, sizeof(int));
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowAddress);
                var listenerPort = unchecked((ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort));
                if (listenerPort == port && processIds.Contains(unchecked((int)row.OwningProcessId)))
                {
                    return true;
                }
                rowAddress = IntPtr.Add(rowAddress, rowSize);
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int AddressFamilyInterNetwork = 2;
    private const int AddressFamilyInterNetworkV6 = 23;

    private enum TcpTableClass
    {
        OwnerPidListener = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MibTcpRowOwnerPid
    {
        public readonly uint State;
        public readonly uint LocalAddress;
        public readonly uint LocalPort;
        public readonly uint RemoteAddress;
        public readonly uint RemotePort;
        public readonly uint OwningProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddress;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddress;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        TcpTableClass tableClass,
        uint reserved);

    private void NotifyClientRunningChanged(bool isRunning)
    {
        try
        {
            ClientRunningChanged?.Invoke(isRunning);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Minecraft process state listener failed: {ex.Message}");
        }
    }

    private void NotifyClientPreparingChanged(bool isPreparing)
    {
        try
        {
            ClientPreparingChanged?.Invoke(isPreparing);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Minecraft preparation state listener failed: {ex.Message}");
        }
    }

    public static bool HasPackData(string packDirectory) => PackManifestService.HasManifest(packDirectory);

    private void EnsureWorldsDirectoryAndSavesLink(string clientDir)
    {
        Directory.CreateDirectory(_paths.Worlds);
        var savesDir = Path.Combine(clientDir, "saves");
        if (TryGetAttributes(savesDir, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = new DirectoryInfo(savesDir).LinkTarget;
                if (IsSamePath(target, _paths.Worlds, clientDir)) return;
                Directory.Delete(savesDir);
            }
            else if (Directory.Exists(savesDir) && !Directory.EnumerateFileSystemEntries(savesDir).Any())
            {
                Directory.Delete(savesDir);
            }
            else
            {
                _logger.Warn("Minecraft saves folder already exists and is not a link; leaving it unchanged to avoid moving worlds.");
                return;
            }
        }

        CreateJunction(savesDir, _paths.Worlds);
        _logger.Info("Minecraft saves folder linked to portable Worlds folder.");
    }

    private static void ValidatePackCompatibility(string packDir)
    {
        ValidateKubeJsArsNouveauPackMetadata(packDir);
    }

    private static void ValidateKubeJsArsNouveauPackMetadata(string packDir)
    {
        var modsDir = Path.Combine(packDir, "mods");
        if (!Directory.Exists(modsDir)) return;
        foreach (var jarPath in Directory.EnumerateFiles(modsDir, "kubejsarsnouveau-*.jar", SearchOption.TopDirectoryOnly))
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var metadataEntry = archive.GetEntry("pack.mcmeta");
            if (metadataEntry is null) continue;
            using var reader = new StreamReader(metadataEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var metadata = reader.ReadToEnd();
            if (metadata.Contains("${pack_format_number}", StringComparison.Ordinal) ||
                metadata.Contains("${mod_id}", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Pack preparation is required: unresolved pack.mcmeta placeholders in {Path.GetFileName(jarPath)}.");
            }
        }
    }

    private void EnsureModernFixShutdownWorkaround(string gameDir)
    {
        var configPath = Path.Combine(gameDir, "config", "modernfix-mixins.properties");
        if (!File.Exists(configPath)) return;
        const string key = "mixin.perf.dedicated_reload_executor";
        const string expected = key + "=false";
        var lines = File.ReadAllLines(configPath).ToList();
        var found = false;
        var changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('#') || !line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
            found = true;
            if (string.Equals(line, expected, StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = expected;
            changed = true;
        }
        if (!found)
        {
            lines.Add(expected);
            changed = true;
        }
        if (!changed) return;
        File.WriteAllLines(configPath, lines);
        _logger.Info("ModernFix dedicated reload executor disabled for reliable singleplayer shutdown.");
    }

    private static string ReadLatestLogTail(string gameDir)
    {
        try
        {
            var path = Path.Combine(gameDir, "logs", "latest.log");
            if (!File.Exists(path)) return "";
            var lines = File.ReadLines(path).TakeLast(30);
            var details = string.Join(Environment.NewLine, lines);
            return details.Length == 0 ? "" : Environment.NewLine + details;
        }
        catch
        {
            return "";
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (IOException)
        {
            attributes = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsSamePath(string? linkTarget, string expectedTarget, string linkParent)
    {
        if (string.IsNullOrWhiteSpace(linkTarget)) return false;
        var resolvedTarget = Path.IsPathRooted(linkTarget)
            ? Path.GetFullPath(linkTarget)
            : Path.GetFullPath(Path.Combine(linkParent, linkTarget));
        return string.Equals(
            resolvedTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(expectedTarget).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode == 0) return;
        throw new InvalidOperationException(
            $"Could not create directory link: {process.StandardError.ReadToEnd()}{process.StandardOutput.ReadToEnd()}".Trim());
    }
}
