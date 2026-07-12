using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CmlLib.Core;
using CmlLib.Core.FileExtractors;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.Version;
using CmlLib.Core.VersionLoader;

namespace Minecraft;

public sealed class PackRuntimeService : IDisposable
{
    private const int RuntimeStateSchemaVersion = 2;
    private const string RuntimeStateFileName = ".portable-runtime.json";
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<PackLoaderKind, IPackLoaderProvider> _providers;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public PackRuntimeService(AppPaths paths, Logger logger, HttpClient? httpClient = null)
    {
        _paths = paths;
        _logger = logger;
        _httpClient = httpClient ?? PortableHttpClient.Shared;
        IPackLoaderProvider[] providers =
        [
            new VanillaLoaderProvider(),
            new FabricLoaderProvider(),
            new QuiltLoaderProvider(),
            new ForgeLoaderProvider(),
            new NeoForgeLoaderProvider()
        ];
        _providers = providers.ToDictionary(provider => provider.Kind);
    }

    public async Task<PreparedRuntime> PrepareAsync(
        string packRelativePath,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await PrepareCoreAsync(packRelativePath, progress, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PreparedRuntime> PrepareCoreAsync(
        string packRelativePath,
        IProgress<RuntimePreparationProgress>? progress,
        CancellationToken token)
    {
        var packDirectory = _paths.CombineUnderPacks(packRelativePath);
        var descriptor = PackManifestService.Load(packDirectory);
        var sourceClientJar = PackManifestService.ResolveClientJarPath(packDirectory, descriptor);
        if (!File.Exists(sourceClientJar))
        {
            throw new FileNotFoundException("The client jar declared by portable-pack.json is missing.", sourceClientJar);
        }
        RejectUnexpectedMinecraftJars(packDirectory, sourceClientJar);

        var runtimeRoot = _paths.CombineUnderRuntimes(packRelativePath);
        var temporaryRoot = Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads", SafePackName(packRelativePath));
        _paths.EnsureUnderRoot(temporaryRoot);
        Directory.CreateDirectory(runtimeRoot);
        TryImportLegacyRuntime(runtimeRoot);

        progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Checking, "Проверка файлов"));
        var statePath = Path.Combine(runtimeRoot, RuntimeStateFileName);
        var state = ReadState(statePath);
        if (state is not null &&
            string.Equals(state.DescriptorHash, descriptor.DescriptorHash, StringComparison.OrdinalIgnoreCase) &&
            ValidateSourceClientJarState(sourceClientJar, state) &&
            ValidateState(runtimeRoot, state))
        {
            CleanupLegacyRuntimeFiles(runtimeRoot, state);
            var clientJar = ResolveStatePath(runtimeRoot, state.ClientJarRelativePath);
            var cachedJavaPath = ResolveStatePath(runtimeRoot, state.JavaPathRelativePath);
            progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Ready, "Сборка готова", 1));
            return new PreparedRuntime(runtimeRoot, state.ProfileId, cachedJavaPath, clientJar, descriptor);
        }

        Directory.CreateDirectory(temporaryRoot);
        var launcher = CreateLauncher(runtimeRoot, temporaryRoot, progress, localOnly: false);
        IVersion baseVersion;
        try
        {
            baseVersion = await RuntimeRetry.RunAsync(
                retryToken => launcher.GetVersionAsync(descriptor.MinecraftVersion, retryToken).AsTask(),
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException(
                $"Minecraft version '{descriptor.MinecraftVersion}' could not be resolved from official metadata. " +
                "Connect to the internet or use an already prepared runtime.", ex);
        }

        var clientFile = await ResolveClientFileAsync(launcher, baseVersion, token).ConfigureAwait(false);
        ValidateClientJar(sourceClientJar, clientFile);
        CopyClientJar(sourceClientJar, clientFile.Path!);

        await RuntimeRetry.RunAsync(
            retryToken => launcher.InstallAsync(baseVersion, cancellationToken: retryToken).AsTask(),
            token).ConfigureAwait(false);
        var javaPath = launcher.GetJavaPath(baseVersion) ?? launcher.GetDefaultJavaPath();
        if (string.IsNullOrWhiteSpace(javaPath) || !File.Exists(javaPath))
        {
            throw new FileNotFoundException("The Java runtime required by this Minecraft version was not prepared.", javaPath);
        }

        progress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.InstallingLoader,
            $"Подготовка {LoaderDisplayName(descriptor.Loader.Type)}"));
        if (!_providers.TryGetValue(descriptor.Loader.Type, out var provider))
        {
            throw new NotSupportedException($"Unsupported loader: {descriptor.Loader.Type}");
        }

        var context = new PackLoaderInstallationContext(
            descriptor,
            runtimeRoot,
            temporaryRoot,
            baseVersion.Id,
            javaPath,
            _httpClient,
            launcher,
            progress,
            _logger);
        var profileId = await provider.InstallAsync(context, token).ConfigureAwait(false);

        launcher = CreateLauncher(runtimeRoot, temporaryRoot, progress, localOnly: true);
        var profile = await RuntimeRetry.RunAsync(
            retryToken => launcher.GetVersionAsync(profileId, retryToken).AsTask(),
            token).ConfigureAwait(false);
        await RuntimeRetry.RunAsync(
            retryToken => launcher.InstallAsync(profile, cancellationToken: retryToken).AsTask(),
            token).ConfigureAwait(false);

        progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Verifying, "Проверка файлов"));
        WindowIconAssetService.Apply(runtimeRoot, profile);
        var requiredFiles = await EnumerateRequiredFilesAsync(launcher, profile, clientFile.Path!, token).ConfigureAwait(false);
        var newState = CreateState(
            runtimeRoot,
            descriptor,
            profileId,
            javaPath,
            sourceClientJar,
            clientFile.Path!,
            requiredFiles,
            token);
        AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(newState, _jsonOptions));
        CleanupLegacyRuntimeFiles(runtimeRoot, newState);
        CleanupLegacyLauncherRoot();
        TryDeleteDirectory(temporaryRoot);
        TryDeleteDirectoryIfEmpty(Path.GetDirectoryName(temporaryRoot)!);

        progress?.Report(new RuntimePreparationProgress(RuntimePreparationStage.Ready, "Сборка готова", 1));
        _logger.Info(
            $"Runtime prepared for {packRelativePath}: Minecraft {descriptor.MinecraftVersion}, " +
            $"{LoaderDisplayName(descriptor.Loader.Type)} {descriptor.Loader.Version}, profile {profileId}.");
        return new PreparedRuntime(runtimeRoot, profileId, javaPath, clientFile.Path!, descriptor);
    }

    public MinecraftLauncher CreateLocalLauncher(PreparedRuntime runtime)
    {
        var temporaryRoot = Path.Combine(_paths.Personal, "Temp", "RuntimeDownloads", "launch");
        return CreateLauncher(runtime.RuntimeRoot, temporaryRoot, progress: null, localOnly: true);
    }

    private MinecraftLauncher CreateLauncher(
        string runtimeRoot,
        string temporaryRoot,
        IProgress<RuntimePreparationProgress>? progress,
        bool localOnly)
    {
        var minecraftPath = new MinecraftPath(runtimeRoot);
        minecraftPath.CreateDirs();
        var parameters = MinecraftLauncherParameters.CreateDefault(minecraftPath, _httpClient);
        var extractors = parameters.FileExtractors ?? throw new InvalidOperationException("CmlLib file extractors were not initialized.");
        foreach (var extractor in extractors.OfType<ClientFileExtractor>().ToArray())
        {
            extractors.Remove(extractor);
        }
        parameters.GameInstaller = new PortableGameInstaller(_httpClient, runtimeRoot, temporaryRoot, progress);
        if (localOnly)
        {
            parameters.VersionLoader = new LocalJsonVersionLoader(minecraftPath);
        }
        else if (parameters.VersionLoader is MojangJsonVersionLoaderV2 mojangLoader)
        {
            mojangLoader.UseLocalManifestWhenError = true;
        }
        return new MinecraftLauncher(parameters);
    }

    private static async Task<GameFile> ResolveClientFileAsync(
        MinecraftLauncher launcher,
        IVersion baseVersion,
        CancellationToken token)
    {
        var extractor = new ClientFileExtractor();
        var files = await extractor.Extract(launcher.MinecraftPath, baseVersion, launcher.RulesContext, token);
        var clientFile = files.SingleOrDefault();
        if (clientFile is null || string.IsNullOrWhiteSpace(clientFile.Path))
        {
            throw new InvalidDataException("Official Minecraft metadata does not contain a client jar artifact.");
        }
        return clientFile;
    }

    private static void ValidateClientJar(string sourcePath, GameFile expected)
    {
        var info = new FileInfo(sourcePath);
        if (expected.Size > 0 && info.Length != expected.Size)
        {
            throw new InvalidDataException(
                $"Client jar size does not match official Minecraft metadata: {info.Length} instead of {expected.Size} bytes.");
        }
        if (!string.IsNullOrWhiteSpace(expected.Hash) &&
            !string.Equals(ComputeSha1(sourcePath), expected.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Client jar SHA-1 does not match official Minecraft metadata.");
        }

        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.GetEntry("net/minecraft/client/main/Main.class") is null)
        {
            throw new InvalidDataException("Client jar does not contain the Minecraft client entry point.");
        }
    }

    private static void CopyClientJar(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath) &&
            new FileInfo(destinationPath).Length == new FileInfo(sourcePath).Length &&
            string.Equals(ComputeSha1(destinationPath), ComputeSha1(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var temporaryPath = destinationPath + ".local-client.part";
        File.Copy(sourcePath, temporaryPath, overwrite: true);
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static async Task<IReadOnlyCollection<string>> EnumerateRequiredFilesAsync(
        MinecraftLauncher launcher,
        IVersion profile,
        string clientJarPath,
        CancellationToken token)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { clientJarPath };
        foreach (var version in profile.EnumerateToParent())
        {
            foreach (var file in await launcher.ExtractFiles(version, token))
            {
                if (!string.IsNullOrWhiteSpace(file.Path) && File.Exists(file.Path)) files.Add(Path.GetFullPath(file.Path));
            }
            var versionJson = launcher.MinecraftPath.GetVersionJsonPath(version.Id);
            if (File.Exists(versionJson)) files.Add(versionJson);
        }
        var localManifest = Path.Combine(launcher.MinecraftPath.Versions, "version_manifest_v2.json");
        if (File.Exists(localManifest)) files.Add(localManifest);
        return files;
    }

    private RuntimeState CreateState(
        string runtimeRoot,
        PackRuntimeDescriptor descriptor,
        string profileId,
        string javaPath,
        string sourceClientJarPath,
        string clientJarPath,
        IReadOnlyCollection<string> requiredFiles,
        CancellationToken token)
    {
        var state = new RuntimeState
        {
            SchemaVersion = RuntimeStateSchemaVersion,
            DescriptorHash = descriptor.DescriptorHash,
            ProfileId = profileId,
            JavaPathRelativePath = ToRelativePath(runtimeRoot, javaPath),
            ClientJarRelativePath = ToRelativePath(runtimeRoot, clientJarPath),
            SourceClientJarSizeBytes = new FileInfo(sourceClientJarPath).Length,
            SourceClientJarLastWriteUtcTicks = File.GetLastWriteTimeUtc(sourceClientJarPath).Ticks,
            SourceClientJarSha1 = ComputeSha1(sourceClientJarPath),
            PreparedAtUtc = DateTimeOffset.UtcNow
        };
        foreach (var path in requiredFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            state.Files[ToRelativePath(runtimeRoot, path)] = new RuntimeFileState
            {
                SizeBytes = info.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = ComputeSha256(path)
            };
        }
        return state;
    }

    private bool ValidateState(string runtimeRoot, RuntimeState state)
    {
        if (state.SchemaVersion != RuntimeStateSchemaVersion ||
            string.IsNullOrWhiteSpace(state.ProfileId) ||
            state.Files.Count == 0)
        {
            return false;
        }

        foreach (var (relativePath, expected) in state.Files)
        {
            string path;
            try
            {
                path = ResolveStatePath(runtimeRoot, relativePath);
            }
            catch
            {
                return false;
            }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.SizeBytes) return false;
            if (info.LastWriteTimeUtc.Ticks != expected.LastWriteUtcTicks &&
                !string.Equals(ComputeSha256(path), expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            return File.Exists(ResolveStatePath(runtimeRoot, state.JavaPathRelativePath)) &&
                   File.Exists(ResolveStatePath(runtimeRoot, state.ClientJarRelativePath));
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateSourceClientJarState(string sourceClientJarPath, RuntimeState state)
    {
        var info = new FileInfo(sourceClientJarPath);
        if (!info.Exists || info.Length != state.SourceClientJarSizeBytes) return false;
        return info.LastWriteTimeUtc.Ticks == state.SourceClientJarLastWriteUtcTicks ||
               string.Equals(ComputeSha1(sourceClientJarPath), state.SourceClientJarSha1, StringComparison.OrdinalIgnoreCase);
    }

    private RuntimeState? ReadState(string statePath)
    {
        try
        {
            if (!File.Exists(statePath)) return null;
            return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(statePath), _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void TryImportLegacyRuntime(string runtimeRoot)
    {
        if (File.Exists(Path.Combine(runtimeRoot, RuntimeStateFileName)) ||
            Directory.EnumerateFileSystemEntries(runtimeRoot).Any())
        {
            return;
        }

        var legacyEntries = new[] { "libraries", "assets", "natives", "versions", "java21-windows-x86-64" };
        var moved = false;
        foreach (var name in legacyEntries)
        {
            var source = Path.Combine(_paths.Launcher, name);
            var destination = Path.Combine(runtimeRoot, name);
            if (!Directory.Exists(source) || Directory.Exists(destination)) continue;
            Directory.Move(source, destination);
            moved = true;
        }
        if (moved) _logger.Info("Legacy fixed runtime moved into the selected pack runtime for verification and reuse.");
    }

    private void CleanupLegacyLauncherRoot()
    {
        var fileNames = new[]
        {
            "Start-MinecraftFromLauncher.ps1", "Start-Minecraft.ps1", "Start-Minecraft.cmd",
            "standalone-classpath.txt", "standalone-jvmargs.txt", "standalone-clientargs.txt",
            "minecraft-window-icon.jar",
            "launcher-bootstrap-manifest.json", "runtime-bootstrap-manifest.json", "bootstrap-manifest.json"
        };
        foreach (var name in fileNames)
        {
            TryDeleteFile(Path.Combine(_paths.Launcher, name));
        }
    }

    private static void CleanupLegacyRuntimeFiles(string runtimeRoot, RuntimeState state)
    {
        foreach (var directoryName in new[] { "java21-windows-x86-64", "natives" })
        {
            var prefix = directoryName + "/";
            var isTracked = state.Files.Keys.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                            state.JavaPathRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                            state.ClientJarRelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            if (!isTracked) TryDeleteDirectory(Path.Combine(runtimeRoot, directoryName));
        }

        var legacyNeoForgeRoot = Path.Combine(runtimeRoot, "libraries", "net", "neoforged", "neoforge");
        if (!Directory.Exists(legacyNeoForgeRoot)) return;
        foreach (var installer in Directory.EnumerateFiles(
                     legacyNeoForgeRoot,
                     "neoforge-*-installer.jar",
                     SearchOption.AllDirectories))
        {
            var relative = ToRelativePath(runtimeRoot, installer);
            if (!state.Files.ContainsKey(relative)) TryDeleteFile(installer);
        }
    }

    private static void RejectUnexpectedMinecraftJars(string packDirectory, string selectedClientJar)
    {
        var selected = Path.GetFullPath(selectedClientJar);
        var unexpected = Directory.EnumerateFiles(packDirectory, "*.jar", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(packDirectory, path).Replace('\\', '/');
                var name = Path.GetFileName(path);
                return !string.Equals(Path.GetFullPath(path), selected, StringComparison.OrdinalIgnoreCase) &&
                       (name.Equals("server.jar", StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith("libraries/com/mojang/minecraft/", StringComparison.OrdinalIgnoreCase) ||
                        !relative.Contains('/') &&
                        name.StartsWith("minecraft-", StringComparison.OrdinalIgnoreCase) &&
                        (name.Contains("-client", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("-server", StringComparison.OrdinalIgnoreCase)));
            })
            .Take(10)
            .ToArray();
        if (unexpected.Length > 0)
        {
            throw new InvalidDataException(
                "Pack contains unexpected Minecraft client/server jar files:" + Environment.NewLine +
                string.Join(Environment.NewLine, unexpected));
        }
    }

    private static string LoaderDisplayName(PackLoaderKind kind) => kind switch
    {
        PackLoaderKind.NeoForge => "NeoForge",
        PackLoaderKind.Forge => "Forge",
        PackLoaderKind.Fabric => "Fabric",
        PackLoaderKind.Quilt => "Quilt",
        _ => "Minecraft"
    };

    private static string SafePackName(string packRelativePath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(packRelativePath.Select(ch => invalid.Contains(ch) || ch is '\\' or '/' ? '_' : ch));
    }

    private static string ToRelativePath(string runtimeRoot, string path)
    {
        var relative = Path.GetRelativePath(runtimeRoot, Path.GetFullPath(path));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException($"Runtime state path escapes runtime root: {path}");
        }
        return relative.Replace('\\', '/');
    }

    private static string ResolveStatePath(string runtimeRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Runtime state contains an invalid path.");
        }
        var root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Runtime state path escapes runtime root.");
        }
        return full;
    }

    [SuppressMessage("Security", "CA5350", Justification = "SHA-1 is required to verify Mojang artifact identities from official metadata.")]
    private static string ComputeSha1(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch { }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class RuntimeState
    {
        public int SchemaVersion { get; set; } = RuntimeStateSchemaVersion;
        public string DescriptorHash { get; set; } = "";
        public string ProfileId { get; set; } = "";
        public string JavaPathRelativePath { get; set; } = "";
        public string ClientJarRelativePath { get; set; } = "";
        public long SourceClientJarSizeBytes { get; set; }
        public long SourceClientJarLastWriteUtcTicks { get; set; }
        public string SourceClientJarSha1 { get; set; } = "";
        public DateTimeOffset PreparedAtUtc { get; set; }
        public Dictionary<string, RuntimeFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RuntimeFileState
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}

internal sealed class VanillaLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Vanilla;
    public Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token) =>
        Task.FromResult(context.BaseVersionId);
}

internal sealed class FabricLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Fabric;
    public async Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var installer = new FabricInstaller(context.HttpClient);
        return await RuntimeRetry.RunAsync(
            retryToken => installer.Install(
                context.Descriptor.MinecraftVersion,
                context.Descriptor.Loader.Version!,
                new MinecraftPath(context.RuntimeRoot)).WaitAsync(retryToken),
            token);
    }
}

internal sealed class QuiltLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Quilt;
    public async Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var installer = new QuiltInstaller(context.HttpClient);
        return await RuntimeRetry.RunAsync(
            retryToken => installer.Install(
                context.Descriptor.MinecraftVersion,
                context.Descriptor.Loader.Version!,
                new MinecraftPath(context.RuntimeRoot)).WaitAsync(retryToken),
            token);
    }
}

internal sealed class ForgeLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.Forge;
    public Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        var minecraftVersion = context.Descriptor.MinecraftVersion;
        var loaderVersion = context.Descriptor.Loader.Version!;
        var artifactVersion = loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion
            : minecraftVersion + "-" + loaderVersion;
        var shortLoaderVersion = loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase)
            ? loaderVersion[(minecraftVersion.Length + 1)..]
            : loaderVersion;
        var escapedVersion = Uri.EscapeDataString(artifactVersion);
        var installerName = $"forge-{artifactVersion}-installer.jar";
        var uri = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{escapedVersion}/{Uri.EscapeDataString(installerName)}";
        var expectedProfile = minecraftVersion + "-forge-" + shortLoaderVersion;
        return OfficialLoaderInstaller.InstallAsync(
            context,
            Path.Combine("installers", "forge", artifactVersion, installerName),
            uri,
            "--installClient",
            [expectedProfile],
            (id, json) => id.Contains("forge", StringComparison.OrdinalIgnoreCase) &&
                          !id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(minecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(loaderVersion, StringComparison.OrdinalIgnoreCase),
            token);
    }
}

internal sealed class NeoForgeLoaderProvider : IPackLoaderProvider
{
    public PackLoaderKind Kind => PackLoaderKind.NeoForge;

    public async Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token)
    {
        var version = context.Descriptor.Loader.Version!;
        var profileId = "neoforge-" + version;
        var installerName = $"neoforge-{version}-installer.jar";
        var uri = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(installerName)}";
        return await OfficialLoaderInstaller.InstallAsync(
            context,
            Path.Combine("installers", "neoforge", version, installerName),
            uri,
            "--install-client",
            [profileId],
            (id, json) => id.Contains("neoforge", StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(context.Descriptor.MinecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                          json.Contains(version, StringComparison.OrdinalIgnoreCase),
            token);
    }
}

internal static class OfficialLoaderInstaller
{
    public static async Task<string> InstallAsync(
        PackLoaderInstallationContext context,
        string installerRelativePath,
        string installerUri,
        string installArgument,
        IReadOnlyCollection<string> expectedProfileIds,
        Func<string, string, bool> profileMatcher,
        CancellationToken token)
    {
        var installerPath = Path.Combine(context.RuntimeRoot, installerRelativePath);
        var sha1 = await DownloadChecksumAsync(context.HttpClient, installerUri + ".sha1", token);
        var gameFile = new GameFile(Path.GetFileName(installerPath))
        {
            Path = installerPath,
            Url = installerUri,
            Hash = sha1
        };
        await context.Launcher.GameInstaller.Install([gameFile], null, null, token);
        await RuntimeRetry.RunAsync(
            retryToken => RunInstallerAsync(context, installerPath, installArgument, retryToken),
            token);

        return FindProfile(context.RuntimeRoot, expectedProfileIds, profileMatcher)
            ?? throw new FileNotFoundException(
                $"Loader installer completed but did not create a launch profile for {context.Descriptor.Loader.Version}.");
    }

    private static async Task<string> DownloadChecksumAsync(HttpClient httpClient, string uri, CancellationToken token)
    {
        var value = await RuntimeRetry.RunAsync(async retryToken =>
        {
            using var response = await httpClient.GetAsync(uri, retryToken);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadAsStringAsync(retryToken)).Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }, token);
        if (value.Length != 40 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("NeoForge Maven SHA-1 checksum is invalid.");
        }
        return value.ToLowerInvariant();
    }

    private static async Task RunInstallerAsync(
        PackLoaderInstallationContext context,
        string installerPath,
        string installArgument,
        CancellationToken token)
    {
        Directory.CreateDirectory(context.TemporaryRoot);
        var javaTemp = Path.Combine(context.TemporaryRoot, "Java");
        Directory.CreateDirectory(javaTemp);
        var profilesPath = Path.Combine(context.RuntimeRoot, "launcher_profiles.json");
        var profilesBackup = File.Exists(profilesPath) ? File.ReadAllBytes(profilesPath) : null;
        AtomicFile.WriteAllText(
            profilesPath,
            $"{{\"profiles\":{{\"portable\":{{\"name\":\"Portable\",\"type\":\"custom\",\"lastVersionId\":\"{EscapeJson(context.BaseVersionId)}\"}}}},\"settings\":{{}},\"version\":3}}");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = context.JavaPath,
                WorkingDirectory = context.TemporaryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add($"-Djava.io.tmpdir={javaTemp}");
            startInfo.ArgumentList.Add("-jar");
            startInfo.ArgumentList.Add(installerPath);
            startInfo.ArgumentList.Add(installArgument);
            startInfo.ArgumentList.Add(context.RuntimeRoot);
            startInfo.Environment["TEMP"] = javaTemp;
            startInfo.Environment["TMP"] = javaTemp;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Loader installer process could not be started.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await Task.WhenAll(stdoutTask, stderrTask);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (details.Length > 3000) details = details[^3000..];
                throw new InvalidOperationException(
                    $"Loader installer failed with exit code {process.ExitCode}." + Environment.NewLine + details.Trim());
            }
        }
        finally
        {
            if (profilesBackup is null)
            {
                try { if (File.Exists(profilesPath)) File.Delete(profilesPath); } catch { }
            }
            else
            {
                File.WriteAllBytes(profilesPath, profilesBackup);
            }
        }
    }

    private static string? FindProfile(
        string runtimeRoot,
        IReadOnlyCollection<string> expectedProfileIds,
        Func<string, string, bool> matcher)
    {
        var versionsRoot = Path.Combine(runtimeRoot, "versions");
        if (!Directory.Exists(versionsRoot)) return null;
        foreach (var id in expectedProfileIds)
        {
            if (File.Exists(Path.Combine(versionsRoot, id, id + ".json"))) return id;
        }
        foreach (var file in Directory.EnumerateFiles(versionsRoot, "*.json", SearchOption.AllDirectories))
        {
            var json = File.ReadAllText(file);
            var id = Path.GetFileNameWithoutExtension(file);
            if (matcher(id, json)) return id;
        }
        return null;
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}

internal static class RuntimeRetry
{
    private const int MaximumAttempts = 3;

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken token)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await action(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaximumAttempts)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token).ConfigureAwait(false);
            }
        }

        throw lastError ?? new InvalidOperationException("Runtime operation failed without an exception.");
    }

    public static Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token) =>
        RunAsync(async retryToken =>
        {
            await action(retryToken).ConfigureAwait(false);
            return true;
        }, token);
}

internal static class WindowIconAssetService
{
    private const string ResourceName = "Minecraft.WindowIconAssets.jar";

    [SuppressMessage("Security", "CA5350", Justification = "Minecraft asset object names use SHA-1 by protocol.")]
    public static void Apply(string runtimeRoot, IVersion profile)
    {
        var assetId = profile.GetInheritedProperty(version => version.AssetIndex?.Id);
        if (string.IsNullOrWhiteSpace(assetId)) return;
        var indexPath = Path.Combine(runtimeRoot, "assets", "indexes", assetId + ".json");
        if (!File.Exists(indexPath)) return;

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (resource is null) return;
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText())!;
        if (!root.TryGetValue("objects", out var objectsElement)) return;
        var objects = JsonSerializer.Deserialize<Dictionary<string, AssetEntry>>(objectsElement.GetRawText())
            ?? new Dictionary<string, AssetEntry>(StringComparer.Ordinal);
        var changed = false;
        foreach (var iconName in new[] { "icon_16x16.png", "icon_32x32.png", "icon_48x48.png", "icon_128x128.png", "icon_256x256.png" })
        {
            var entry = archive.GetEntry("icons/" + iconName) ?? archive.GetEntry("assets/minecraft/icons/" + iconName);
            if (entry is null) continue;
            using var memory = new MemoryStream();
            using (var input = entry.Open()) input.CopyTo(memory);
            var bytes = memory.ToArray();
            var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            var objectPath = Path.Combine(runtimeRoot, "assets", "objects", hash[..2], hash);
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
            if (!File.Exists(objectPath)) File.WriteAllBytes(objectPath, bytes);
            foreach (var key in new[] { "icons/" + iconName, "icons/snapshot/" + iconName })
            {
                if (!objects.ContainsKey(key)) continue;
                objects[key] = new AssetEntry { Hash = hash, Size = bytes.Length };
                changed = true;
            }
        }
        if (!changed) return;
        root["objects"] = JsonSerializer.SerializeToElement(objects);
        AtomicFile.WriteAllText(indexPath, JsonSerializer.Serialize(root));
    }

    private sealed class AssetEntry
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
