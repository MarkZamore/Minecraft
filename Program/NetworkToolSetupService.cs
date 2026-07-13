using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Minecraft;

public sealed class NetworkToolSetupService
{
    private const string DescriptorResourceName = "Minecraft.RecommendedNetworkTool.json";
    private static readonly JsonSerializerOptions DescriptorJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly NetworkToolDescriptor _descriptor;

    public NetworkToolSetupService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
        _descriptor = LoadDescriptor();
        ValidateDescriptor(_descriptor);
    }

    public string DisplayName => _descriptor.DisplayName;
    public string ToolId => _descriptor.Id;

    public NetworkToolInstallInfo GetInstallInfo()
    {
        var registryPath = FindFromRegistry();
        if (!string.IsNullOrWhiteSpace(registryPath) && File.Exists(registryPath))
        {
            return new NetworkToolInstallInfo(true, registryPath);
        }

        foreach (var candidate in GetCommonExecutableCandidates())
        {
            if (File.Exists(candidate))
            {
                return new NetworkToolInstallInfo(true, candidate);
            }
        }

        return new NetworkToolInstallInfo(false, null);
    }

    public async Task<string> DownloadInstallerAsync(IProgress<string>? progress, CancellationToken token)
    {
        var installerUri = new Uri(_descriptor.InstallerUri, UriKind.Absolute);
        ValidateDownloadUri(installerUri);
        Directory.CreateDirectory(_paths.Personal);

        var installerPath = Path.Combine(_paths.Personal, Path.GetFileName(installerUri.LocalPath));
        if (File.Exists(installerPath) && new FileInfo(installerPath).Length > 1024 * 1024)
        {
            progress?.Report($"{DisplayName} installer already downloaded in Minecraft folder.");
            return installerPath;
        }

        progress?.Report($"Downloading {DisplayName} to Minecraft folder...");
        using var response = await PortableHttpClient.Shared.GetAsync(
            installerUri,
            HttpCompletionOption.ResponseHeadersRead,
            token);
        response.EnsureSuccessStatusCode();
        ValidateDownloadUri(response.RequestMessage?.RequestUri ?? installerUri);

        var temporaryPath = installerPath + ".download";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }

            var length = new FileInfo(temporaryPath).Length;
            if (length < 1024 * 1024)
            {
                throw new InvalidOperationException($"Downloaded {DisplayName} installer is unexpectedly small.");
            }
            File.Move(temporaryPath, installerPath, overwrite: true);

            progress?.Report($"{DisplayName} installer downloaded to Minecraft folder: {Path.GetFileName(installerPath)}");
            _logger.Info($"Network tool installer downloaded from approved host: {installerUri.Host}; path={installerPath}; bytes={length}");
            return installerPath;
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public async Task InstallAndCleanupAsync(string installerPath, IProgress<string>? progress, CancellationToken token)
    {
        using var process = RunInstaller(installerPath)
            ?? throw new InvalidOperationException($"{DisplayName} installer did not start.");
        progress?.Report($"{DisplayName} installer started. Waiting for installation to finish...");
        await process.WaitForExitAsync(token);

        var installInfo = await WaitForInstalledAsync(TimeSpan.FromMinutes(2), token);
        if (!installInfo.IsInstalled)
        {
            throw new InvalidOperationException($"{DisplayName} installation was not confirmed. Installer was kept in Minecraft folder.");
        }

        DeleteInstallerIfExists(installerPath);
        DeleteLegacyInstallers();
        progress?.Report($"{DisplayName} installed. Installer removed.");
    }

    public Process? RunInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException($"{DisplayName} installer was not found.", installerPath);
        }

        ValidateInstallerSignature(installerPath);
        var isMsi = string.Equals(_descriptor.InstallerKind, "msi", StringComparison.OrdinalIgnoreCase);
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = isMsi ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : installerPath,
            Arguments = isMsi ? $"/i \"{installerPath}\"" : string.Empty,
            WorkingDirectory = _paths.Personal,
            UseShellExecute = true,
            Verb = "runas"
        });
        _logger.Info("Network tool installer started.");
        return process;
    }

    public bool Launch()
    {
        var installInfo = GetInstallInfo();
        if (!installInfo.IsInstalled || string.IsNullOrWhiteSpace(installInfo.ExecutablePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = installInfo.ExecutablePath,
            UseShellExecute = true
        });
        _logger.Info("Configured network tool started.");
        return true;
    }

    public NetworkCredentials GenerateCredentials(string playerName)
    {
        var cleanPlayer = new string(playerName.Where(char.IsLetterOrDigit).Take(10).ToArray());
        if (string.IsNullOrWhiteSpace(cleanPlayer))
        {
            cleanPlayer = Environment.UserName;
        }

        var suffix = RandomNumberGenerator.GetHexString(4);
        var password = $"{RandomNumberGenerator.GetHexString(4)}-{RandomNumberGenerator.GetHexString(4)}-{RandomNumberGenerator.GetHexString(4)}";
        return new NetworkCredentials($"Minecraft-{cleanPlayer}-{suffix}", password);
    }

    private void ValidateInstallerSignature(string installerPath)
    {
        var escapedPath = installerPath.Replace("'", "''", StringComparison.Ordinal);
        var command = "$s = Get-AuthenticodeSignature -LiteralPath '" + escapedPath +
                      "'; Write-Output ($s.Status.ToString() + '|' + $s.SignerCertificate.Subject)";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not validate the network tool installer signature.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 ||
            !output.StartsWith("Valid|", StringComparison.OrdinalIgnoreCase) ||
            !output.Contains(_descriptor.SignerSubjectContains, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Network tool installer signature is not valid.{(string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error)}");
        }
    }

    private async Task<NetworkToolInstallInfo> WaitForInstalledAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var installInfo = GetInstallInfo();
            if (installInfo.IsInstalled)
            {
                return installInfo;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        return GetInstallInfo();
    }

    private void DeleteInstallerIfExists(string installerPath)
    {
        try
        {
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
                _logger.Info($"Network tool installer removed: {installerPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not remove network tool installer: {ex.Message}");
        }
    }

    private void DeleteLegacyInstallers()
    {
        var installerFileName = Path.GetFileName(new Uri(_descriptor.InstallerUri).LocalPath);
        DeleteInstallerIfExists(Path.Combine(_paths.Service, installerFileName));
        DeleteInstallerIfExists(Path.Combine(_paths.Program, installerFileName));
    }

    private void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Network tool installer must be downloaded over HTTPS.");
        }

        if (!_descriptor.AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Blocked unapproved network tool download host: {uri.Host}");
        }
    }

    private string? FindFromRegistry()
    {
        foreach (var registryPath in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                 })
        {
            using var root = Registry.LocalMachine.OpenSubKey(registryPath);
            if (root is null) continue;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                var displayName = subKey?.GetValue("DisplayName") as string;
                if (displayName is null ||
                    !_descriptor.RegistryDisplayNameContains.Any(value =>
                        displayName.Contains(value, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var installLocation = subKey?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    foreach (var fileName in _descriptor.ExecutableNames)
                    {
                        var candidate = Path.Combine(installLocation, fileName);
                        if (File.Exists(candidate)) return candidate;
                    }
                }

                var displayIcon = subKey?.GetValue("DisplayIcon") as string;
                var iconPath = NormalizeRegistryExecutablePath(displayIcon);
                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                {
                    return iconPath;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> GetCommonExecutableCandidates()
    {
        var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            foreach (var directoryName in _descriptor.InstallDirectoryNames)
            {
                foreach (var fileName in _descriptor.ExecutableNames)
                {
                    yield return Path.Combine(root, directoryName, fileName);
                }
            }
        }
    }

    private static string? NormalizeRegistryExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().Trim('"');
        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? trimmed[..(exeIndex + 4)] : trimmed;
    }

    private static NetworkToolDescriptor LoadDescriptor()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DescriptorResourceName)
            ?? throw new InvalidOperationException("Embedded network tool descriptor was not found.");
        return JsonSerializer.Deserialize<NetworkToolDescriptor>(stream, DescriptorJsonOptions)
            ?? throw new InvalidOperationException("Embedded network tool descriptor is invalid.");
    }

    private static void ValidateDescriptor(NetworkToolDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id) ||
            string.IsNullOrWhiteSpace(descriptor.DisplayName) ||
            !Uri.TryCreate(descriptor.InstallerUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            descriptor.AllowedDownloadHosts.Count == 0 ||
            !descriptor.AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(descriptor.SignerSubjectContains) ||
            descriptor.RegistryDisplayNameContains.Count == 0 ||
            descriptor.InstallDirectoryNames.Count == 0 ||
            descriptor.ExecutableNames.Count == 0)
        {
            throw new InvalidOperationException("Embedded network tool descriptor is incomplete.");
        }
    }
}

public sealed class NetworkToolDescriptor
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string InstallerUri { get; set; } = "";
    public List<string> AllowedDownloadHosts { get; set; } = [];
    public string SignerSubjectContains { get; set; } = "";
    public List<string> RegistryDisplayNameContains { get; set; } = [];
    public List<string> InstallDirectoryNames { get; set; } = [];
    public List<string> ExecutableNames { get; set; } = [];
    public string InstallerKind { get; set; } = "executable";
}

public sealed record NetworkToolInstallInfo(bool IsInstalled, string? ExecutablePath);

public sealed record NetworkCredentials(string NetworkName, string Password);
