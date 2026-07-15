using System.Diagnostics;
using System.IO;
using System.Text;

namespace Minecraft;

public static class LogCleanupService
{
    public static void RunCleanup(AppPaths paths)
    {
        CleanupLauncherLog(paths.LogFile);
        CleanupMinecraftGeneratedFiles(paths.Personal);
        CleanupInstanceGeneratedFiles(paths.Instances);
        try
        {
            PackInstanceService.CleanupEmptyWorldPlaceholders(paths.Worlds);
        }
        catch
        {
        }
        CleanupDotNetExtractionCache();
    }

    public static void ScheduleCurrentExtractionCleanup(AppPaths paths, int processId)
    {
        var extractionDir = FindCurrentExtractionDirectory();
        if (string.IsNullOrWhiteSpace(extractionDir)) return;

        try
        {
            var escapedExtractionDir = extractionDir.Replace("'", "''", StringComparison.Ordinal);
            var command = "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
                          $"$processId = {processId}\r\n" +
                          $"$extractionDir = '{escapedExtractionDir}'\r\n" +
                          "function Test-ExtractionInUse {\r\n" +
                          "  foreach ($candidate in @(Get-Process -Name 'Minecraft' -ErrorAction SilentlyContinue)) {\r\n" +
                          "    if ($candidate.Id -eq $processId) { continue }\r\n" +
                          "    try {\r\n" +
                          "      foreach ($module in @($candidate.Modules)) {\r\n" +
                          "        if ($module.FileName.StartsWith($extractionDir, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }\r\n" +
                          "      }\r\n" +
                          "    } catch { return $true }\r\n" +
                          "  }\r\n" +
                          "  return $false\r\n" +
                          "}\r\n" +
                          "Wait-Process -Id $processId -Timeout 120\r\n" +
                          "Start-Sleep -Seconds 15\r\n" +
                          "if (Test-ExtractionInUse) { exit 0 }\r\n" +
                          "if (-not (Get-Process -Id $processId)) {\r\n" +
                          "  for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $extractionDir); $attempt++) {\r\n" +
                          "    Start-Sleep -Milliseconds 500\r\n" +
                          "    Remove-Item -LiteralPath $extractionDir -Recurse -Force\r\n" +
                          "  }\r\n" +
                          "}\r\n" +
                          "$extractionParent = Split-Path -Parent $extractionDir\r\n" +
                          "if ((Test-Path -LiteralPath $extractionParent) -and -not (Get-ChildItem -LiteralPath $extractionParent -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $extractionParent -Force }\r\n";
            var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                WorkingDirectory = paths.Personal,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
        }
    }

    private static void CleanupLauncherLog(string launcherLogPath)
    {
        var directory = Path.GetDirectoryName(launcherLogPath);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        DeleteFile(launcherLogPath);
        foreach (var archivedLog in Directory.EnumerateFiles(directory, "logs-*.log", SearchOption.TopDirectoryOnly))
        {
            DeleteFile(archivedLog);
        }
    }

    private static void CleanupMinecraftGeneratedFiles(string personalRoot)
    {
        Directory.CreateDirectory(personalRoot);
        foreach (var directoryName in new[] { "BootstrapDownloads", "Build", "Temp" })
        {
            DeleteDirectory(Path.Combine(personalRoot, directoryName));
        }

        CleanupUpdateArtifacts(Path.Combine(personalRoot, "Updates"));

        var logsRoot = Path.Combine(personalRoot, "Logs");
        if (!Directory.Exists(logsRoot)) return;
        foreach (var file in Directory.EnumerateFiles(logsRoot, "*", SearchOption.AllDirectories)
                     .Select(path => new FileInfo(path))
                     .Where(ShouldDeleteGeneratedFile)
                     .ToList())
        {
            DeleteFile(file.FullName);
        }
    }

    private static void CleanupUpdateArtifacts(string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot)) return;

        foreach (var fileName in new[]
                 {
                     $"{UpdateService.ExecutableAssetName}.download",
                     $"{UpdateService.DeltaPatchAssetName}.download",
                     $"{UpdateService.ExecutableAssetName}.new",
                     "apply-update.ps1",
                     $"{UpdateService.ExecutableAssetName}.bak",
                     "update.log"
                 })
        {
            DeleteFile(Path.Combine(updatesRoot, fileName));
        }

        foreach (var file in Directory.EnumerateFiles(updatesRoot, "*.bsdiff.download", SearchOption.TopDirectoryOnly))
        {
            DeleteFile(file);
        }
        TryDeleteDirectoryIfEmpty(updatesRoot);
    }

    private static bool ShouldDeleteGeneratedFile(FileInfo file)
    {
        if (file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directory = file.DirectoryName ?? string.Empty;
        return directory.Contains("crash-reports", StringComparison.OrdinalIgnoreCase) ||
               directory.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
               directory.Contains("dynamic-data-pack-cache", StringComparison.OrdinalIgnoreCase) ||
               directory.Contains("dynamic-resource-pack-cache", StringComparison.OrdinalIgnoreCase);
    }

    private static void CleanupInstanceGeneratedFiles(string instancesRoot)
    {
        if (!Directory.Exists(instancesRoot)) return;
        var generatedDirectories = new[]
        {
            ".mixin.out",
            "logs",
            "crash-reports",
            "debug",
            "downloads",
            "dynamic-data-pack-cache",
            "dynamic-resource-pack-cache"
        };
        foreach (var instanceDir in Directory.EnumerateDirectories(instancesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            foreach (var directoryName in generatedDirectories)
            {
                DeleteDirectory(Path.Combine(instanceDir, directoryName));
            }
            try
            {
                PackInstanceService.CleanupDisposableInstancePlaceholders(instanceDir);
            }
            catch
            {
            }
        }
    }

    private static void CleanupDotNetExtractionCache()
    {
        var root = GetExtractionRoot();
        if (!Directory.Exists(root)) return;
        var inUse = FindExtractionDirectoriesInUse(root);
        if (inUse is null) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (inUse.Contains(Path.GetFullPath(directory))) continue;
            DeleteDirectory(directory);
        }
        TryDeleteDirectoryIfEmpty(root);
    }

    private static HashSet<string>? FindExtractionDirectoriesInUse(string root)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootWithSlash = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcessesByName("Minecraft"))
        {
            using (process)
            {
                try
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        var directory = GetExtractionDirectory(root, rootWithSlash, module.FileName);
                        if (directory is not null) directories.Add(directory);
                    }
                }
                catch
                {
                    if (!process.HasExited) return null;
                }
            }
        }

        return directories;
    }

    private static string? FindCurrentExtractionDirectory()
    {
        var root = GetExtractionRoot();
        var rootWithSlash = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            {
                var directory = GetExtractionDirectory(root, rootWithSlash, module.FileName);
                if (directory is not null) return directory;
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? GetExtractionDirectory(string root, string rootWithSlash, string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath)) return null;
        var full = Path.GetFullPath(modulePath);
        if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = full[rootWithSlash.Length..];
        var separator = relative.IndexOf(Path.DirectorySeparatorChar);
        var directoryName = separator < 0 ? relative : relative[..separator];
        return string.IsNullOrWhiteSpace(directoryName) ? null : Path.GetFullPath(Path.Combine(root, directoryName));
    }

    private static string GetExtractionRoot() => Path.Combine(Path.GetTempPath(), ".net", "Minecraft");

    private static void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
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
}
