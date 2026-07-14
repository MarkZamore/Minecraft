using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class PortableIdentityAdapterService : IDisposable
{
    private const int StateSchemaVersion = 1;
    private const string ResourceName = "Minecraft.PortableIdentityAdapter.jar";
    private const string AdapterFileName = "portable-identity-adapter.jar";
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly IdentityAdapterMappingService _mappings = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PortableIdentityAdapterService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> PrepareJvmArgumentsAsync(
        PreparedRuntime runtime,
        CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(_paths.Launcher, "IdentityAdapters", runtime.Descriptor.DescriptorHash);
            _paths.EnsureUnderRoot(directory);
            Directory.CreateDirectory(directory);
            var adapterPath = ExtractAdapter(directory);
            var adapterHash = HashFile(adapterPath);
            var statePath = Path.Combine(directory, "adapter-state.json");
            var state = ReadState(statePath);
            if (state is not null && IsStateValid(state, runtime, adapterHash))
            {
                return CreateJvmArguments(adapterPath, state.Properties);
            }

            IdentityAdapterConfiguration configuration;
            try
            {
                configuration = _mappings.Build(runtime);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
            {
                throw new NotSupportedException(
                    $"Portable UUID adapter is not compatible with Minecraft {runtime.Descriptor.MinecraftVersion} " +
                    $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}. " +
                    "Update Minecraft.exe before starting this pack.",
                    ex);
            }

            foreach (var target in configuration.Targets)
            {
                await RunPreflightAsync(runtime.JavaPath, adapterPath, configuration.Properties, target, token)
                    .ConfigureAwait(false);
                if (IsConfiguredAlias(configuration.Properties, "textureUrlCheckerClasses", target.ClassName))
                {
                    await RunSkinSemanticPreflightAsync(
                            runtime.JavaPath,
                            adapterPath,
                            configuration.Properties,
                            target,
                            token)
                        .ConfigureAwait(false);
                }
            }

            var mappingInfo = new FileInfo(configuration.MappingPath);
            var targetStates = configuration.Targets.Select(target =>
            {
                var info = new FileInfo(target.JarPath);
                return new IdentityAdapterTargetState
                {
                    JarPath = target.JarPath,
                    ClassName = target.ClassName,
                    SizeBytes = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sha256 = HashFile(target.JarPath)
                };
            }).ToList();
            state = new IdentityAdapterState
            {
                SchemaVersion = StateSchemaVersion,
                DescriptorHash = runtime.Descriptor.DescriptorHash,
                AdapterSha256 = adapterHash,
                MappingPath = configuration.MappingPath,
                MappingSizeBytes = mappingInfo.Length,
                MappingLastWriteUtcTicks = mappingInfo.LastWriteTimeUtc.Ticks,
                MappingSha256 = HashFile(configuration.MappingPath),
                Properties = configuration.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                Targets = targetStates,
                VerifiedAtUtc = DateTimeOffset.UtcNow
            };
            state.ConfigurationSha256 = ComputeConfigurationHash(state);
            AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, _jsonOptions));
            _logger.Info(
                $"Portable UUID adapter verified for {runtime.Descriptor.MinecraftVersion} " +
                $"{runtime.Descriptor.Loader.Type} {runtime.Descriptor.Loader.Version}.");
            return CreateJvmArguments(adapterPath, state.Properties);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ExtractAdapter(string directory)
    {
        var destination = Path.Combine(directory, AdapterFileName);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded portable identity adapter is missing.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (File.Exists(destination) && string.Equals(HashFile(destination), expectedHash, StringComparison.Ordinal))
        {
            return destination;
        }

        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (!string.Equals(HashFile(temporary), expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Extracted portable identity adapter failed SHA-256 validation.");
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return destination;
    }

    private static async Task RunPreflightAsync(
        string configuredJavaPath,
        string adapterPath,
        IReadOnlyDictionary<string, string> properties,
        IdentityAdapterTarget target,
        CancellationToken token)
    {
        var javaPath = ResolveConsoleJava(configuredJavaPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--add-exports=java.base/jdk.internal.org.objectweb.asm=ALL-UNNAMED");
        startInfo.ArgumentList.Add("--add-exports=java.base/jdk.internal.org.objectweb.asm.tree=ALL-UNNAMED");
        foreach (var pair in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add($"-Dminecraft.portable.identity.{pair.Key}={pair.Value}");
        }
        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(adapterPath);
        startInfo.ArgumentList.Add("minecraft.portable.identity.PortableIdentityPreflight");
        startInfo.ArgumentList.Add(target.JarPath);
        startInfo.ArgumentList.Add(target.ClassName);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Portable identity preflight process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(token);
        var standardError = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Portable identity bytecode preflight timed out.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var output = (await standardOutput.ConfigureAwait(false)).Trim();
        var error = (await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, new[] { output, error }.Where(value => value.Length > 0));
            if (details.Length > 2000) details = details[^2000..];
            throw new NotSupportedException(
                $"Portable UUID bytecode preflight failed for {Path.GetFileName(target.JarPath)}::{target.ClassName}. " + details);
        }
    }

    private static async Task RunSkinSemanticPreflightAsync(
        string configuredJavaPath,
        string adapterPath,
        IReadOnlyDictionary<string, string> properties,
        IdentityAdapterTarget target,
        CancellationToken token)
    {
        const string playerUuid = "00000000-0000-4000-8000-000000000001";
        const string skinHash = "1111111111111111111111111111111111111111111111111111111111111111";
        const string otherHash = "2222222222222222222222222222222222222222222222222222222222222222";
        var registeredUrl = $"http://127.0.0.1:{SkinService.HttpPort}/skin/{playerUuid}/{skinHash}";
        var unregisteredUrl = $"http://127.0.0.1:{SkinService.HttpPort}/skin/{playerUuid}/{otherHash}";
        const string officialUrl = "https://textures.minecraft.net/texture/portable-preflight";
        var registryPath = Path.Combine(
            Path.GetDirectoryName(adapterPath)!,
            $".skin-preflight-{Guid.NewGuid():N}.properties");

        try
        {
            File.WriteAllText(
                registryPath,
                $"{playerUuid}|{skinHash}|classic|{registeredUrl}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveConsoleJava(configuredJavaPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--add-exports=java.base/jdk.internal.org.objectweb.asm=ALL-UNNAMED");
            startInfo.ArgumentList.Add("--add-exports=java.base/jdk.internal.org.objectweb.asm.tree=ALL-UNNAMED");
            startInfo.ArgumentList.Add("-Dminecraft.portable.identity.enabled=true");
            startInfo.ArgumentList.Add($"-Dminecraft.portable.skin.registry={registryPath}");
            foreach (var pair in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                startInfo.ArgumentList.Add($"-Dminecraft.portable.identity.{pair.Key}={pair.Value}");
            }
            startInfo.ArgumentList.Add($"-javaagent:{adapterPath}");
            startInfo.ArgumentList.Add("-cp");
            startInfo.ArgumentList.Add(adapterPath + Path.PathSeparator + target.JarPath);
            startInfo.ArgumentList.Add("minecraft.portable.identity.PortableSkinPreflight");
            startInfo.ArgumentList.Add(registeredUrl);
            startInfo.ArgumentList.Add(unregisteredUrl);
            startInfo.ArgumentList.Add(officialUrl);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Portable skin preflight process could not be started.");
            var standardOutput = process.StandardOutput.ReadToEndAsync(token);
            var standardError = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                TryKill(process);
                throw;
            }

            var output = (await standardOutput.ConfigureAwait(false)).Trim();
            var error = (await standardError.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
            {
                var details = string.Join(Environment.NewLine, new[] { output, error }.Where(value => value.Length > 0));
                if (details.Length > 2000) details = details[^2000..];
                throw new NotSupportedException(
                    $"Portable skin behavior preflight failed for {Path.GetFileName(target.JarPath)}. " + details);
            }
        }
        finally
        {
            if (File.Exists(registryPath)) File.Delete(registryPath);
        }
    }

    private static bool IsConfiguredAlias(
        IReadOnlyDictionary<string, string> properties,
        string propertyName,
        string value) =>
        properties.TryGetValue(propertyName, out var aliases) &&
        aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(value, StringComparer.Ordinal);

    private static List<string> CreateJvmArguments(
        string adapterPath,
        IReadOnlyDictionary<string, string> properties)
    {
        var arguments = new List<string>
        {
            "-Dminecraft.portable.identity.enabled=true",
            "--add-exports=java.base/jdk.internal.org.objectweb.asm=ALL-UNNAMED",
            "--add-exports=java.base/jdk.internal.org.objectweb.asm.tree=ALL-UNNAMED"
        };
        arguments.AddRange(properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"-Dminecraft.portable.identity.{pair.Key}={pair.Value}"));
        arguments.Add($"-javaagent:{adapterPath}");
        return arguments;
    }

    private bool IsStateValid(IdentityAdapterState state, PreparedRuntime runtime, string adapterHash)
    {
        if (state.SchemaVersion != StateSchemaVersion ||
            !string.Equals(state.DescriptorHash, runtime.Descriptor.DescriptorHash, StringComparison.Ordinal) ||
            !string.Equals(state.AdapterSha256, adapterHash, StringComparison.Ordinal) ||
            state.Properties.Count == 0 ||
            state.Targets.Count == 0 ||
            !FileMatches(state.MappingPath, state.MappingSizeBytes, state.MappingLastWriteUtcTicks))
        {
            return false;
        }
        _paths.EnsureUnderRoot(state.MappingPath);
        foreach (var target in state.Targets)
        {
            _paths.EnsureUnderRoot(target.JarPath);
            if (!FileMatches(target.JarPath, target.SizeBytes, target.LastWriteUtcTicks)) return false;
        }
        return string.Equals(state.ConfigurationSha256, ComputeConfigurationHash(state), StringComparison.Ordinal);
    }

    private IdentityAdapterState? ReadState(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<IdentityAdapterState>(File.ReadAllText(path), _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Portable identity adapter state will be regenerated: {ex.Message}");
            return null;
        }
    }

    private static string ComputeConfigurationHash(IdentityAdapterState state)
    {
        var text = new StringBuilder()
            .AppendLine(state.DescriptorHash)
            .AppendLine(state.AdapterSha256)
            .AppendLine(state.MappingSha256);
        foreach (var pair in state.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(pair.Key).Append('=').AppendLine(pair.Value);
        }
        foreach (var target in state.Targets.OrderBy(target => target.ClassName, StringComparer.Ordinal))
        {
            text.Append(target.ClassName).Append('=').AppendLine(target.Sha256);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static bool FileMatches(string path, long size, long lastWriteUtcTicks)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == lastWriteUtcTicks;
    }

    private static string ResolveConsoleJava(string configuredPath)
    {
        var directory = Path.GetDirectoryName(configuredPath)
            ?? throw new FileNotFoundException("Java runtime path is invalid.", configuredPath);
        var consoleJava = Path.Combine(directory, "java.exe");
        if (File.Exists(consoleJava)) return consoleJava;
        if (File.Exists(configuredPath)) return configuredPath;
        throw new FileNotFoundException("Java runtime required for identity preflight was not found.", consoleJava);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private sealed class IdentityAdapterState
    {
        public int SchemaVersion { get; set; }
        public string DescriptorHash { get; set; } = "";
        public string AdapterSha256 { get; set; } = "";
        public string MappingPath { get; set; } = "";
        public long MappingSizeBytes { get; set; }
        public long MappingLastWriteUtcTicks { get; set; }
        public string MappingSha256 { get; set; } = "";
        public string ConfigurationSha256 { get; set; } = "";
        public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
        public List<IdentityAdapterTargetState> Targets { get; set; } = [];
        public DateTimeOffset VerifiedAtUtc { get; set; }
    }

    private sealed class IdentityAdapterTargetState
    {
        public string JarPath { get; set; } = "";
        public string ClassName { get; set; } = "";
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
