using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Minecraft;

public sealed record PackInstanceContext(string PackDirectory, string GameDirectory, string ClientJar);

public sealed class PackInstanceService : IDisposable
{
    private const int StateSchemaVersion = 1;
    private const string StateFileName = ".portable-instance.json";

    private static readonly HashSet<string> LegacyDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mixin.out",
        "blueprints",
        "crash-reports",
        "debug",
        "downloads",
        "dynamic-data-pack-cache",
        "dynamic-resource-pack-cache",
        "ftbbackups3",
        "ldlib2",
        "local",
        "logs",
        "moddata",
        "moonlight-global-datapacks",
        "saves",
        "schematics",
        "screenshots",
        "xaero"
    };

    private static readonly HashSet<string> LegacyFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "command_history.txt",
        "observable_announce",
        "options.txt",
        "patchouli_data.json",
        "servers.dat",
        "servers.dat_old",
        "usercache.json",
        "usernamecache.json"
    };

    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public PackInstanceService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string GetInstanceDirectory(string packRelativePath) => _paths.CombineUnderInstances(packRelativePath);

    public async Task<PackInstanceContext> PrepareAsync(string packRelativePath, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => PrepareCore(packRelativePath, token), token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CleanupGeneratedLocalArtifactsAsync(string packRelativePath, bool removeSessionLogs)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var gameDir = GetInstanceDirectory(packRelativePath);
            if (Directory.Exists(gameDir))
            {
                SanitizeInstanceForLocalPlay(gameDir, Path.GetFileName(packRelativePath));
                if (removeSessionLogs)
                {
                    DeleteDirectoryIfPresent(Path.Combine(gameDir, "logs"));
                    DeleteDirectoryIfPresent(Path.Combine(gameDir, "debug"));
                    DeleteDirectoryIfPresent(Path.Combine(gameDir, "crash-reports"));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private PackInstanceContext PrepareCore(string packRelativePath, CancellationToken token)
    {
        var packDir = _paths.CombineUnderPacks(packRelativePath);
        var gameDir = GetInstanceDirectory(packRelativePath);
        if (!Directory.Exists(packDir) || !PackManifestService.HasManifest(packDir))
        {
            throw new DirectoryNotFoundException($"Minecraft pack is missing {PackManifestService.ManifestFileName}: {packDir}");
        }
        var descriptor = PackManifestService.Load(packDir);
        var clientJar = PackManifestService.ResolveClientJarPath(packDir, descriptor);
        if (!File.Exists(clientJar))
        {
            throw new FileNotFoundException("Minecraft client jar is missing from the selected pack.", clientJar);
        }

        Directory.CreateDirectory(gameDir);
        var statePath = Path.Combine(gameDir, StateFileName);
        var state = ReadState(statePath, packRelativePath);
        if (!state.LegacyMigrationCompleted)
        {
            MigrateLegacyPersonalData(packDir, gameDir, packRelativePath, descriptor.ClientJar, token);
            state.LegacyMigrationCompleted = true;
        }

        EnsureMods(packDir, gameDir, state, token);
        SynchronizePackFiles(packDir, gameDir, packRelativePath, descriptor.ClientJar, state, token);
        SanitizeInstanceForLocalPlay(gameDir, Path.GetFileName(packRelativePath));
        state.SchemaVersion = StateSchemaVersion;
        state.PackRelativePath = packRelativePath;
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, _jsonOptions));
        return new PackInstanceContext(packDir, gameDir, clientJar);
    }

    private void SynchronizePackFiles(
        string packDir,
        string gameDir,
        string packRelativePath,
        string clientJarName,
        InstanceState state,
        CancellationToken token)
    {
        var previousFiles = new Dictionary<string, SourceFileState>(state.Files, StringComparer.OrdinalIgnoreCase);
        var currentFiles = new Dictionary<string, SourceFileState>(StringComparer.OrdinalIgnoreCase);
        string? conflictRoot = null;
        var conflictCount = 0;

        foreach (var directory in EnumerateSourceDirectories(packDir, clientJarName))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(packDir, directory);
            var destination = Path.Combine(gameDir, relative);
            if (File.Exists(destination))
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                var preservedFile = Path.Combine(conflictRoot, relative + ".user-file");
                Directory.CreateDirectory(Path.GetDirectoryName(preservedFile)!);
                File.Copy(destination, preservedFile, overwrite: true);
                File.Delete(destination);
                conflictCount++;
            }
            Directory.CreateDirectory(destination);
        }

        foreach (var sourcePath in EnumerateSourceFiles(packDir, clientJarName))
        {
            token.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(packDir, sourcePath));
            previousFiles.TryGetValue(relative, out var previous);
            var source = ReadSourceState(sourcePath, previous);
            currentFiles[relative] = source;
            var destination = Path.Combine(gameDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(destination))
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                conflictCount++;
                continue;
            }
            var destinationExists = File.Exists(destination);

            if (previous is null)
            {
                if (!destinationExists)
                {
                    CopySourceFile(sourcePath, destination);
                }
                else if (!HashesEqual(HashFile(destination), source.Sha256))
                {
                    conflictRoot ??= CreateConflictRoot(packRelativePath);
                    CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                    conflictCount++;
                }
                continue;
            }

            if (HashesEqual(source.Sha256, previous.Sha256))
            {
                continue;
            }

            if (!destinationExists)
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                conflictCount++;
                continue;
            }

            var destinationHash = HashFile(destination);
            if (HashesEqual(destinationHash, previous.Sha256))
            {
                CopySourceFile(sourcePath, destination);
            }
            else if (!HashesEqual(destinationHash, source.Sha256))
            {
                conflictRoot ??= CreateConflictRoot(packRelativePath);
                CopySourceFile(sourcePath, Path.Combine(conflictRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                conflictCount++;
            }
        }

        foreach (var removed in previousFiles.Where(entry => !currentFiles.ContainsKey(entry.Key)))
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(gameDir, removed.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination)) continue;
            if (HashesEqual(HashFile(destination), removed.Value.Sha256))
            {
                File.Delete(destination);
                DeleteEmptyParents(Path.GetDirectoryName(destination), gameDir);
            }
            else
            {
                _logger.Warn($"Pack removed {removed.Key}, but the locally modified instance file was preserved.");
            }
        }

        state.Files = currentFiles;
        if (conflictCount > 0)
        {
            _logger.Warn($"Pack instance synchronization preserved {conflictCount} local conflict(s). New pack files: {conflictRoot}");
        }
    }

    private void EnsureMods(string packDir, string gameDir, InstanceState state, CancellationToken token)
    {
        var source = Path.Combine(packDir, "mods");
        var destination = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(source))
        {
            if (TryGetAttributes(destination, out var existingAttributes) &&
                (existingAttributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(destination);
            }
            else if (Directory.Exists(destination))
            {
                foreach (var (relative, previous) in state.ModFiles)
                {
                    token.ThrowIfCancellationRequested();
                    var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path) && HashesEqual(HashFile(path), previous.Sha256)) File.Delete(path);
                }
            }
            Directory.CreateDirectory(destination);
            state.ModsMode = "Empty";
            state.ModFiles.Clear();
            return;
        }

        if (TryGetAttributes(destination, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(destination);
        }

        var managedModsDirectory =
            string.Equals(state.ModsMode, "HardLink", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.ModsMode, "Copy", StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(destination) &&
            !managedModsDirectory &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var conflictRoot = CreateConflictRoot(state.PackRelativePath);
            var preserved = Path.Combine(conflictRoot, "mods-local");
            Directory.Move(destination, preserved);
            _logger.Warn($"Existing instance mods were preserved at {preserved}.");
        }

        Directory.CreateDirectory(destination);
        var allHardLinks = MirrorMods(source, destination, state, token);
        state.ModsMode = allHardLinks ? "HardLink" : "Copy";
    }

    private static bool MirrorMods(string sourceDir, string destinationDir, InstanceState state, CancellationToken token)
    {
        var current = new Dictionary<string, SourceFileState>(StringComparer.OrdinalIgnoreCase);
        var allHardLinks = true;
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(Path.GetRelativePath(sourceDir, sourcePath));
            state.ModFiles.TryGetValue(relative, out var previous);
            var source = ReadSourceState(sourcePath, previous);
            current[relative] = source;
            var destination = Path.Combine(destinationDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination) ||
                new FileInfo(destination).Length != source.SizeBytes ||
                !HashesEqual(HashFile(destination), source.Sha256))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination)) File.Delete(destination);
                if (!TryCreateHardLink(destination, sourcePath))
                {
                    CopySourceFile(sourcePath, destination);
                    allHardLinks = false;
                }
            }
            else if (!string.Equals(state.ModsMode, "HardLink", StringComparison.OrdinalIgnoreCase))
            {
                allHardLinks = false;
            }
        }

        foreach (var file in Directory.EnumerateFiles(destinationDir, "*", SearchOption.AllDirectories).ToArray())
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(destinationDir, file));
            if (!current.ContainsKey(relative)) File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(destinationDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        state.ModFiles = current;
        return allHardLinks;
    }

    private void MigrateLegacyPersonalData(
        string packDir,
        string gameDir,
        string packRelativePath,
        string clientJarName,
        CancellationToken token)
    {
        var conflictRoot = Path.Combine(_paths.PackConflicts, SafePackName(packRelativePath), $"migration-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        var migrated = false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(packDir).ToArray())
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            if (string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, clientJarName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PackManifestService.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entry) &&
                (LegacyDirectories.Contains(name) || name.StartsWith("XaeroWaypoints_BACKUP", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.Equals(name, "saves", StringComparison.OrdinalIgnoreCase))
                {
                    MigrateLegacySaves(entry, conflictRoot);
                }
                else
                {
                    MigrateLegacyDirectory(entry, Path.Combine(gameDir, name), conflictRoot);
                }
                migrated = true;
            }
            else if (File.Exists(entry) && LegacyFiles.Contains(name))
            {
                MoveLegacyFile(entry, Path.Combine(gameDir, name), conflictRoot);
                migrated = true;
            }
        }

        var legacyGeneratedRoot = Path.Combine(_paths.Personal, "Logs", "Packs_" + SafePackName(packRelativePath));
        if (Directory.Exists(legacyGeneratedRoot))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(legacyGeneratedRoot).ToArray())
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    MoveDirectoryContents(entry, Path.Combine(gameDir, name), Path.Combine(conflictRoot, name));
                }
                else if (File.Exists(entry))
                {
                    MoveLegacyFile(entry, Path.Combine(gameDir, name), conflictRoot);
                }
            }
            TryDeleteDirectoryIfEmpty(legacyGeneratedRoot);
            TryDeleteDirectoryIfEmpty(Path.GetDirectoryName(legacyGeneratedRoot)!);
            migrated = true;
        }

        if (migrated)
        {
            _logger.Info($"Migrated legacy writable pack data into Personal/Instances/{packRelativePath}.");
        }
        TryDeleteDirectoryIfEmpty(conflictRoot);
    }

    private void MigrateLegacySaves(string savesPath, string conflictRoot)
    {
        if (TryGetAttributes(savesPath, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(savesPath);
            return;
        }

        Directory.CreateDirectory(_paths.Worlds);
        MoveDirectoryContents(savesPath, _paths.Worlds, Path.Combine(conflictRoot, "saves"));
        TryDeleteDirectoryIfEmpty(savesPath);
    }

    private void MigrateLegacyDirectory(string sourcePath, string destinationPath, string conflictRoot)
    {
        if (TryGetAttributes(sourcePath, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = ResolveLinkTarget(sourcePath);
            _paths.EnsureUnderRoot(target);
            if (Directory.Exists(target))
            {
                MoveDirectoryContents(target, destinationPath, Path.Combine(conflictRoot, Path.GetFileName(sourcePath)));
                TryDeleteDirectoryIfEmpty(target);
            }
            Directory.Delete(sourcePath);
            return;
        }

        MoveDirectoryContents(sourcePath, destinationPath, Path.Combine(conflictRoot, Path.GetFileName(sourcePath)));
        TryDeleteDirectoryIfEmpty(sourcePath);
    }

    private static void MoveDirectoryContents(string sourceDir, string destinationDir, string conflictDir)
    {
        if (!Directory.Exists(sourceDir)) return;
        if (!Directory.Exists(destinationDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationDir)!);
            Directory.Move(sourceDir, destinationDir);
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDir).ToArray())
        {
            MoveDirectoryContents(directory, Path.Combine(destinationDir, Path.GetFileName(directory)), Path.Combine(conflictDir, Path.GetFileName(directory)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceDir).ToArray())
        {
            MoveLegacyFile(file, Path.Combine(destinationDir, Path.GetFileName(file)), conflictDir);
        }
        TryDeleteDirectoryIfEmpty(sourceDir);
    }

    private static void MoveLegacyFile(string source, string destination, string conflictDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            File.Move(source, destination);
            return;
        }
        if (HashesEqual(HashFile(source), HashFile(destination)))
        {
            File.Delete(source);
            return;
        }

        Directory.CreateDirectory(conflictDir);
        File.Move(source, Path.Combine(conflictDir, Path.GetFileName(source)), overwrite: true);
    }

    private IEnumerable<string> EnumerateSourceFiles(string packDir, string clientJarName)
    {
        foreach (var entry in EnumerateCanonicalEntries(packDir, clientJarName))
        {
            if (File.Exists(entry)) yield return entry;
        }
    }

    private IEnumerable<string> EnumerateSourceDirectories(string packDir, string clientJarName)
    {
        foreach (var entry in EnumerateCanonicalEntries(packDir, clientJarName))
        {
            if (Directory.Exists(entry)) yield return entry;
        }
    }

    private IEnumerable<string> EnumerateCanonicalEntries(string packDir, string clientJarName)
    {
        var pending = new Stack<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(packDir))
        {
            var name = Path.GetFileName(entry);
            if (string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, clientJarName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PackManifestService.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                LegacyDirectories.Contains(name) ||
                LegacyFiles.Contains(name) ||
                name.StartsWith("XaeroWaypoints_BACKUP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entry))
            {
                if (!ShouldExcludeDirectory(packDir, entry)) pending.Push(entry);
            }
            else if (File.Exists(entry) && !ShouldExcludeSourceFile(entry))
            {
                yield return entry;
            }
        }

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Pack contains an unsupported directory link: {directory}");
            }
            yield return directory;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    if (!ShouldExcludeDirectory(packDir, entry)) pending.Push(entry);
                }
                else if (File.Exists(entry) && !ShouldExcludeSourceFile(entry))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool ShouldExcludeDirectory(string packDir, string directory)
    {
        var relative = NormalizeRelativePath(Path.GetRelativePath(packDir, directory));
        return relative.Equals("config/jei/world/server", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("config/jei/world/server/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldExcludeSourceFile(string path)
    {
        if (!path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 1024 * 1024) return false;
            var text = File.ReadAllText(path);
            return text.Contains("serverIP", StringComparison.OrdinalIgnoreCase) &&
                   text.Contains("serverProxyIP", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void SanitizeInstanceForLocalPlay(string gameDir, string buildName)
    {
        var externalServerHistory = Path.Combine(gameDir, "config", "jei", "world", "server");
        if (Directory.Exists(externalServerHistory)) Directory.Delete(externalServerHistory, recursive: true);

        var configRoot = Path.Combine(gameDir, "config");
        if (Directory.Exists(configRoot))
        {
            foreach (var file in Directory.EnumerateFiles(configRoot, "*.toml", SearchOption.AllDirectories).ToArray())
            {
                if (!ShouldExcludeSourceFile(file)) continue;
                File.Delete(file);
                DeleteEmptyParents(Path.GetDirectoryName(file), configRoot);
            }
        }

        var clientConfigPath = Path.Combine(gameDir, "kubejs", "config", "client.json");
        if (!File.Exists(clientConfigPath)) return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(clientConfigPath)) as JsonObject;
            if (root is null || root["window_title"] is null) return;
            var title = string.IsNullOrWhiteSpace(buildName) ? "Minecraft" : buildName.Trim();
            if (string.Equals(root["window_title"]?.GetValue<string>(), title, StringComparison.Ordinal)) return;
            root["window_title"] = title;
            AtomicFile.WriteAllText(clientConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
        }
    }

    private InstanceState ReadState(string statePath, string packRelativePath)
    {
        try
        {
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<InstanceState>(File.ReadAllText(statePath), _jsonOptions);
                if (state?.SchemaVersion == StateSchemaVersion)
                {
                    state.Files = new Dictionary<string, SourceFileState>(state.Files, StringComparer.OrdinalIgnoreCase);
                    state.ModFiles = new Dictionary<string, SourceFileState>(state.ModFiles, StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.Warn($"Pack instance state could not be read and will be rebuilt: {ex.Message}");
        }

        return new InstanceState { PackRelativePath = packRelativePath };
    }

    private string CreateConflictRoot(string packRelativePath)
    {
        var root = Path.Combine(
            _paths.PackConflicts,
            SafePackName(packRelativePath),
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture));
        _paths.EnsureUnderRoot(root);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string SafePackName(string relativePath) => relativePath
        .Replace(Path.DirectorySeparatorChar, '_')
        .Replace(Path.AltDirectorySeparatorChar, '_');

    private static SourceFileState ReadSourceState(string path, SourceFileState? previous)
    {
        var info = new FileInfo(path);
        if (previous is not null &&
            previous.SizeBytes == info.Length &&
            previous.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks &&
            !string.IsNullOrWhiteSpace(previous.Sha256))
        {
            return new SourceFileState
            {
                SizeBytes = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = previous.Sha256
            };
        }

        return new SourceFileState
        {
            SizeBytes = info.Length,
            LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
            Sha256 = HashFile(path)
        };
    }

    private static void CopySourceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        File.Copy(source, destination, overwrite: true);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool HashesEqual(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static void DeleteEmptyParents(string? path, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrWhiteSpace(path) &&
               !string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()) return;
            Directory.Delete(path);
            path = Path.GetDirectoryName(path);
        }
    }

    private static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return CreateHardLink(linkPath, targetPath, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    private static string ResolveLinkTarget(string linkPath)
    {
        var target = new DirectoryInfo(linkPath).LinkTarget
            ?? throw new InvalidDataException($"Directory link has no target: {linkPath}");
        return Path.IsPathRooted(target)
            ? Path.GetFullPath(target)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));
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

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class InstanceState
    {
        public int SchemaVersion { get; set; } = StateSchemaVersion;
        public string PackRelativePath { get; set; } = "";
        public bool LegacyMigrationCompleted { get; set; }
        public string ModsMode { get; set; } = "";
        public Dictionary<string, SourceFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourceFileState> ModFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SourceFileState
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
