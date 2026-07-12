using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Minecraft;

public sealed class PortableIdentityAdapterService
{
    private const string ResourceName = "Minecraft.PortableIdentityAdapter.jar";
    private const string AdapterFileName = "portable-identity-adapter-1.21.1.jar";
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public PortableIdentityAdapterService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public static bool IsSupported(PackRuntimeDescriptor descriptor)
    {
        return descriptor.MinecraftVersion == "1.21.1" &&
               descriptor.Loader.Type == PackLoaderKind.NeoForge &&
               descriptor.Loader.Version == "21.1.224";
    }

    public Guid ResolveMinecraftUuid(LocalIdentityContext identity, PackRuntimeDescriptor descriptor)
    {
        if (IsSupported(descriptor) && Guid.TryParse(identity.IdentityId, out var portableUuid))
        {
            return portableUuid;
        }

        return WorldPlayerProfileService.CreateOfflinePlayerUuid(identity.IdentityName);
    }

    public IReadOnlyList<string> PrepareJvmArguments(PackRuntimeDescriptor descriptor, string gameDirectory)
    {
        if (!IsSupported(descriptor)) return Array.Empty<string>();
        var adapterPath = ExtractAdapter(gameDirectory);
        return
        [
            "-Dminecraft.portable.identity.enabled=true",
            "--add-exports=java.base/jdk.internal.org.objectweb.asm=ALL-UNNAMED",
            "--add-exports=java.base/jdk.internal.org.objectweb.asm.tree=ALL-UNNAMED",
            $"-javaagent:{adapterPath}"
        ];
    }

    private string ExtractAdapter(string gameDirectory)
    {
        var directory = Path.Combine(gameDirectory, ".portable-runtime", "IdentityAdapters");
        _paths.EnsureUnderRoot(directory);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, AdapterFileName);
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded portable identity adapter is missing.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var bytes = memory.ToArray();
        if (File.Exists(destination) &&
            SHA256.HashData(File.ReadAllBytes(destination)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            return destination;
        }

        var temporary = destination + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        _logger.Info("Portable identity adapter prepared for Minecraft 1.21.1 NeoForge 21.1.224.");
        return destination;
    }
}
