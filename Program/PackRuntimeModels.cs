using System.Net.Http;

namespace Minecraft;

public enum PackLoaderKind
{
    Vanilla,
    Forge,
    NeoForge,
    Fabric,
    Quilt
}

public sealed record PackLoaderDescriptor(PackLoaderKind Type, string? Version);

public sealed record PackRuntimeDescriptor(
    int SchemaVersion,
    string MinecraftVersion,
    PackLoaderDescriptor Loader,
    string ClientJar,
    string DescriptorHash);

public enum RuntimePreparationStage
{
    Idle,
    Checking,
    Downloading,
    InstallingLoader,
    Verifying,
    Ready,
    Failed
}

public sealed record RuntimePreparationProgress(
    RuntimePreparationStage Stage,
    string Message,
    double? Fraction = null,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    int PhaseIndex = 0,
    int PhaseCount = 0);

public sealed record PreparedRuntime(
    string RuntimeRoot,
    string ProfileId,
    string JavaPath,
    string ClientJarPath,
    PackRuntimeDescriptor Descriptor);

public interface IPackLoaderProvider
{
    PackLoaderKind Kind { get; }

    Task<string> InstallAsync(PackLoaderInstallationContext context, CancellationToken token);
}

public sealed record PackLoaderInstallationContext(
    PackRuntimeDescriptor Descriptor,
    string RuntimeRoot,
    string TemporaryRoot,
    string BaseVersionId,
    string JavaPath,
    HttpClient HttpClient,
    CmlLib.Core.MinecraftLauncher Launcher,
    IProgress<RuntimePreparationProgress>? Progress,
    Logger Logger);
