using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Minecraft;

public sealed class RadminVpnSetupService
{
    private static readonly Uri OfficialInstallerUri = new("https://download.radmin-vpn.com/download/files/Radmin_VPN_2.0.4899.9.exe");
    private readonly AppPaths _paths;
    private readonly Logger _logger;

    public RadminVpnSetupService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public string DownloadUrl => OfficialInstallerUri.ToString();

    public RadminVpnInstallInfo GetInstallInfo()
    {
        var registryPath = FindFromRegistry();
        if (!string.IsNullOrWhiteSpace(registryPath) && File.Exists(registryPath))
        {
            return new RadminVpnInstallInfo(true, registryPath);
        }

        foreach (var candidate in GetCommonExecutableCandidates())
        {
            if (File.Exists(candidate))
            {
                return new RadminVpnInstallInfo(true, candidate);
            }
        }

        return new RadminVpnInstallInfo(false, null);
    }

    public async Task<string> DownloadInstallerAsync(IProgress<string>? progress, CancellationToken token)
    {
        ValidateOfficialDownloadUri(OfficialInstallerUri);
        Directory.CreateDirectory(_paths.Personal);

        var installerPath = Path.Combine(_paths.Personal, Path.GetFileName(OfficialInstallerUri.LocalPath));
        if (File.Exists(installerPath) && new FileInfo(installerPath).Length > 1024 * 1024)
        {
            progress?.Report("Radmin VPN installer already downloaded in Minecraft folder.");
            return installerPath;
        }

        progress?.Report("Downloading Radmin VPN to Minecraft folder...");
        using var response = await PortableHttpClient.Shared.GetAsync(OfficialInstallerUri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(installerPath);
        await input.CopyToAsync(output, token);

        var length = new FileInfo(installerPath).Length;
        if (length < 1024 * 1024)
        {
            File.Delete(installerPath);
            throw new InvalidOperationException("Downloaded Radmin VPN installer is unexpectedly small.");
        }

        progress?.Report($"Radmin VPN installer downloaded to Minecraft folder: {Path.GetFileName(installerPath)}");
        _logger.Info($"Radmin VPN installer downloaded from official host: {OfficialInstallerUri.Host}; path={installerPath}; bytes={length}");
        return installerPath;
    }

    public async Task InstallAndCleanupAsync(string installerPath, IProgress<string>? progress, CancellationToken token)
    {
        using var process = RunInstaller(installerPath) ?? throw new InvalidOperationException("Radmin VPN installer did not start.");
        progress?.Report("Radmin VPN installer started. Waiting for installation to finish...");
        await process.WaitForExitAsync(token);

        var installInfo = await WaitForInstalledAsync(TimeSpan.FromMinutes(2), token);
        if (!installInfo.IsInstalled)
        {
            throw new InvalidOperationException("Radmin VPN installation was not confirmed. Installer was kept in Minecraft folder.");
        }

        DeleteInstallerIfExists(installerPath);
        DeleteLegacyInstallers();
        progress?.Report("Radmin VPN installed. Installer removed.");
    }

    public Process? RunInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("Radmin VPN installer was not found.", installerPath);
        }

        ValidateInstallerSignature(installerPath);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = _paths.Personal,
            UseShellExecute = true,
            Verb = "runas"
        });
        _logger.Info("Radmin VPN installer started.");
        return process;
    }

    private static void ValidateInstallerSignature(string installerPath)
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
            ?? throw new InvalidOperationException("Could not validate the Radmin VPN installer signature.");
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 ||
            !output.StartsWith("Valid|", StringComparison.OrdinalIgnoreCase) ||
            !output.Contains("Famatech", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Radmin VPN installer signature is not valid.{(string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error)}");
        }
    }

    public bool LaunchRadminVpn()
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
        _logger.Info("Radmin VPN started.");
        return true;
    }

    public async Task<NetworkAdapterInfo?> WaitForAdapterAsync(VirtualNetworkService network, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var adapter = FindInstalledAdapter(network.GetAdapters());
            if (adapter is not null)
            {
                return adapter;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        return null;
    }

    public NetworkAdapterInfo? FindInstalledAdapter(IEnumerable<NetworkAdapterInfo> adapters)
    {
        return adapters.FirstOrDefault(IsInstalledAdapter);
    }

    public RadminNetworkTicket GenerateNetworkTicket(string playerName)
    {
        var cleanPlayer = new string(playerName.Where(char.IsLetterOrDigit).Take(10).ToArray());
        if (string.IsNullOrWhiteSpace(cleanPlayer))
        {
            cleanPlayer = Environment.UserName;
        }

        var suffix = RandomNumberGenerator.GetHexString(4);
        var password = $"{RandomNumberGenerator.GetHexString(4)}-{RandomNumberGenerator.GetHexString(4)}-{RandomNumberGenerator.GetHexString(4)}";
        return new RadminNetworkTicket($"Minecraft-{cleanPlayer}-{suffix}", password);
    }

    private async Task<RadminVpnInstallInfo> WaitForInstalledAsync(TimeSpan timeout, CancellationToken token)
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
                _logger.Info($"Radmin VPN installer removed: {installerPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not remove Radmin VPN installer: {ex.Message}");
        }
    }

    private void DeleteLegacyInstallers()
    {
        var installerFileName = Path.GetFileName(OfficialInstallerUri.LocalPath);
        DeleteInstallerIfExists(Path.Combine(_paths.Service, installerFileName));
        DeleteInstallerIfExists(Path.Combine(_paths.Program, installerFileName));
    }

    private static void ValidateOfficialDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Radmin VPN installer must be downloaded over HTTPS.");
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("download.radmin-vpn.com" or "www.radmin-vpn.com" or "radmin-vpn.com"))
        {
            throw new InvalidOperationException($"Blocked non-official Radmin VPN download host: {uri.Host}");
        }
    }

    private static string? FindFromRegistry()
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
                if (displayName is null || !displayName.Contains("Radmin VPN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var installLocation = subKey?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation))
                {
                    foreach (var fileName in GetExecutableFileNames())
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

    private static IEnumerable<string> GetCommonExecutableCandidates()
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var fileName in GetExecutableFileNames())
            {
                yield return Path.Combine(root, "Radmin VPN", fileName);
            }
        }
    }

    private static IEnumerable<string> GetExecutableFileNames()
    {
        yield return "RadminVPN.exe";
        yield return "RvRvpnGui.exe";
        yield return "Radmin VPN.exe";
    }

    private static bool IsInstalledAdapter(NetworkAdapterInfo adapter)
    {
        if (adapter.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) ||
            adapter.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return adapter.IPv4.StartsWith("26.", StringComparison.Ordinal);
    }

    private static string? NormalizeRegistryExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().Trim('"');
        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? trimmed[..(exeIndex + 4)] : trimmed;
    }
}

public sealed record RadminVpnInstallInfo(bool IsInstalled, string? ExecutablePath);

public sealed record RadminNetworkTicket(string NetworkName, string Password);
