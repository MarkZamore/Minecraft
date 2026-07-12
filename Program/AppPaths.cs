using System.IO;

namespace Minecraft;

public sealed class AppPaths
{
    public static string ResolveApplicationRoot()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                return processDirectory;
            }
        }

        return AppContext.BaseDirectory;
    }

    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
        var driveRoot = Path.GetPathRoot(Root);
        if (string.Equals(Root.TrimEnd('\\'), driveRoot?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Application root cannot be a drive root.");
        }

        Service = CombineUnderRoot("Minecraft");
        Program = CombineUnderRoot("Program");
        Packs = CombineUnderService("Packs");
        Launcher = CombineUnderService("Launcher");
        Runtimes = Path.Combine(Launcher, "Runtimes");
        Personal = CombineUnderService("Personal");
        Instances = Path.Combine(Personal, "Instances");
        PackConflicts = Path.Combine(Personal, "PackConflicts");
        Worlds = CombineUnderService("Worlds");
    }

    public string Root { get; }
    public string Service { get; }
    public string Program { get; }
    public string Packs { get; }
    public string Launcher { get; }
    public string Runtimes { get; }
    public string Personal { get; }
    public string Instances { get; }
    public string PackConflicts { get; }
    public string Worlds { get; }
    public string SettingsFile => Path.Combine(Personal, "settings.json");
    public string IdentityFile => Path.Combine(Personal, "UUID.json");
    public string NetworkPeersFile => Path.Combine(Personal, "network-peers.json");
    public string PackHashesFile => Path.Combine(Personal, "pack-hashes.json");
    public string[] LegacySettingsFiles => new[]
    {
        Path.Combine(Service, "settings.json"),
        Path.Combine(Program, "settings.json")
    };
    public string LogFile => Path.Combine(Personal, "logs.log");

    public void Ensure()
    {
        Directory.CreateDirectory(Service);
        Directory.CreateDirectory(Packs);
        Directory.CreateDirectory(Launcher);
        Directory.CreateDirectory(Runtimes);
        Directory.CreateDirectory(Personal);
        Directory.CreateDirectory(Instances);
        Directory.CreateDirectory(Worlds);
    }

    public string CombineUnderRoot(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        EnsureUnderRoot(full);
        return full;
    }

    public string CombineUnderService(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Service, relativePath));
        EnsureUnderRoot(full);
        return full;
    }

    public string CombineUnderPacks(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Packs, relativePath));
        EnsureUnderDirectory(full, Packs);
        return full;
    }

    public string CombineUnderInstances(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Instances, relativePath));
        EnsureUnderDirectory(full, Instances);
        return full;
    }

    public string CombineUnderRuntimes(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Runtimes, relativePath));
        EnsureUnderDirectory(full, Runtimes);
        return full;
    }

    public void EnsureUnderRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var rootWithSlash = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes portable root: {path}");
        }
    }

    private static void EnsureUnderDirectory(string path, string parent)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var basePath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(full, basePath, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes portable directory: {path}");
        }
    }
}
