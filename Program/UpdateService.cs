using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minecraft;

public sealed class UpdateService
{
    public const string RepositoryOwner = "MarkZamore";
    public const string RepositoryName = "Minecraft";
    public const string ReleaseTag = "latest";
    public const string ExecutableAssetName = "Minecraft.exe";
    public const string ManifestAssetName = "update.json";

    private static readonly Uri LatestReleaseApiUri = new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/tags/{ReleaseTag}");
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _currentCommitSha;
    private readonly TimeSpan _checkTimeout;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public UpdateService(
        AppPaths paths,
        Logger logger,
        HttpClient? httpClient = null,
        string? currentCommitSha = null,
        TimeSpan? checkTimeout = null)
    {
        _paths = paths;
        _logger = logger;
        _httpClient = httpClient ?? PortableHttpClient.Shared;
        _currentCommitSha = string.IsNullOrWhiteSpace(currentCommitSha) ? CurrentCommitSha : NormalizeSha(currentCommitSha);
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(5);
    }

    public static string CurrentCommitSha => ResolveCurrentCommitSha();
    public static string CurrentCommitShortSha => ShortSha(CurrentCommitSha);

    public PreparedUpdate? TryGetPreparedUpdate()
    {
        var updatesDir = GetUpdatesDirectory(create: false);
        var executablePath = Path.Combine(updatesDir, ExecutableAssetName);
        var manifestPath = Path.Combine(updatesDir, ManifestAssetName);
        var hasExecutable = File.Exists(executablePath);
        var hasManifest = File.Exists(manifestPath);
        if (!hasExecutable && !hasManifest)
        {
            return null;
        }

        if (!hasExecutable || !hasManifest)
        {
            _logger.Warn("Incomplete cached update was removed.");
            DeletePreparedUpdate(executablePath, manifestPath);
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(manifestPath), _jsonOptions)
                ?? throw new InvalidOperationException("Cached update manifest is empty.");
            ValidateManifest(manifest);

            var evaluation = EvaluateManifest(manifest, _currentCommitSha);
            if (!evaluation.IsUpdateAvailable)
            {
                DeletePreparedUpdate(executablePath, manifestPath);
                return null;
            }

            ValidateDownloadedFile(executablePath, manifest);
            return new PreparedUpdate(manifest, executablePath);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Cached update is invalid and was removed: {ex.Message}");
            DeletePreparedUpdate(executablePath, manifestPath);
            return null;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_checkTimeout);
            var release = await GetLatestReleaseAsync(timeout.Token);
            var manifestAsset = release.FindAsset(ManifestAssetName);
            var executableAsset = release.FindAsset(ExecutableAssetName);
            if (manifestAsset is null || executableAsset is null)
            {
                return UpdateCheckResult.UpToDate("Release assets are incomplete.");
            }

            var manifest = await DownloadManifestAsync(manifestAsset.BrowserDownloadUrl, timeout.Token);
            ValidateManifest(manifest);
            if (executableAsset.Size > 0 && executableAsset.Size != manifest.SizeBytes)
            {
                throw new InvalidOperationException("Release asset size does not match update manifest.");
            }
            var evaluation = EvaluateManifest(manifest, _currentCommitSha);
            if (!evaluation.IsUpdateAvailable)
            {
                return evaluation;
            }

            return UpdateCheckResult.Available(manifest, executableAsset.BrowserDownloadUrl);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update check failed: {ex.Message}");
            return UpdateCheckResult.UpToDate(ex.Message);
        }
    }

    public async Task<PreparedUpdate> DownloadUpdateAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken token)
    {
        if (!update.IsUpdateAvailable || update.Manifest is null || update.ExecutableDownloadUrl is null)
        {
            throw new InvalidOperationException("No update is available.");
        }

        ValidateGitHubDownloadUri(update.ExecutableDownloadUrl);
        ValidateManifest(update.Manifest);
        var updatesDir = GetUpdatesDirectory(create: true);
        var downloadPath = Path.Combine(updatesDir, $"{ExecutableAssetName}.download");
        DeleteFileIfExists(downloadPath);
        try
        {
            using var response = await _httpClient.GetAsync(update.ExecutableDownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var expectedSize = update.Manifest.SizeBytes;
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value != expectedSize)
            {
                throw new InvalidOperationException("Downloaded update size does not match manifest.");
            }

            progress?.Report(0d);
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(
                             downloadPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await CopyWithProgressAsync(input, output, expectedSize, progress, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }

            ValidateDownloadedFile(downloadPath, update.Manifest);
            var readyPath = Path.Combine(updatesDir, ExecutableAssetName);
            File.Move(downloadPath, readyPath, overwrite: true);

            var manifestPath = Path.Combine(updatesDir, ManifestAssetName);
            AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(update.Manifest, _jsonOptions));
            return new PreparedUpdate(update.Manifest, readyPath);
        }
        catch
        {
            DeleteFileIfExists(downloadPath);
            throw;
        }
    }

    public void StartInstallAndRestart(string newExecutablePath)
    {
        if (!File.Exists(newExecutablePath))
        {
            throw new FileNotFoundException("Downloaded update was not found.", newExecutablePath);
        }

        var currentExe = Environment.ProcessPath ?? Path.Combine(_paths.Root, ExecutableAssetName);
        var updatesDir = GetUpdatesDirectory(create: true);
        var scriptPath = Path.Combine(updatesDir, "apply-update.ps1");
        var backupPath = Path.Combine(updatesDir, $"{ExecutableAssetName}.bak");
        var logPath = Path.Combine(updatesDir, "update.log");
        AtomicFile.WriteAllText(scriptPath, BuildUpdaterScript(), Encoding.UTF8);

        var arguments =
            "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) +
            " -PidToWait " + Environment.ProcessId +
            " -ExePath " + Quote(currentExe) +
            " -NewExePath " + Quote(newExecutablePath) +
            " -BackupPath " + Quote(backupPath) +
            " -LogPath " + Quote(logPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            WorkingDirectory = _paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static UpdateCheckResult EvaluateManifest(UpdateManifest manifest, string? currentCommitSha)
    {
        if (string.IsNullOrWhiteSpace(manifest.CommitSha))
        {
            return UpdateCheckResult.UpToDate("Manifest commit SHA is empty.");
        }

        if (string.IsNullOrWhiteSpace(currentCommitSha))
        {
            return UpdateCheckResult.UpToDate("Current build has no embedded commit SHA.");
        }

        return string.Equals(NormalizeSha(manifest.CommitSha), NormalizeSha(currentCommitSha), StringComparison.OrdinalIgnoreCase)
            ? UpdateCheckResult.UpToDate()
            : UpdateCheckResult.Available(manifest, null);
    }

    public static void ValidateDownloadedFile(string path, UpdateManifest manifest)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Downloaded update was not found.", path);
        }

        if (manifest.SizeBytes > 0 && file.Length != manifest.SizeBytes)
        {
            throw new InvalidOperationException("Downloaded update size does not match manifest.");
        }

        var expectedSha = manifest.Sha256?.Trim() ?? "";
        if (expectedSha.Length == 0)
        {
            throw new InvalidOperationException("Update manifest is missing SHA-256.");
        }

        using var stream = File.OpenRead(path);
        var actualSha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Downloaded update SHA-256 does not match manifest.");
        }
    }

    public static void ValidateManifest(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.CommitSha))
        {
            throw new InvalidOperationException("Update manifest is missing commit SHA.");
        }

        if (!string.Equals(manifest.AssetName, ExecutableAssetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update manifest contains an unexpected asset name.");
        }

        if (manifest.SizeBytes <= 0)
        {
            throw new InvalidOperationException("Update manifest contains an invalid file size.");
        }

        var sha256 = manifest.Sha256?.Trim() ?? string.Empty;
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("Update manifest contains an invalid SHA-256.");
        }
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken token)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseApiUri, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, _jsonOptions, token) ??
               throw new InvalidOperationException("GitHub release response was empty.");
    }

    private async Task<UpdateManifest> DownloadManifestAsync(Uri manifestUri, CancellationToken token)
    {
        ValidateGitHubDownloadUri(manifestUri);
        await using var stream = await _httpClient.GetStreamAsync(manifestUri, token);
        return await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, _jsonOptions, token) ??
               throw new InvalidOperationException("Update manifest was empty.");
    }

    private static async Task CopyWithProgressAsync(Stream input, Stream output, long expectedSize, IProgress<double>? progress, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            total += read;
            if (expectedSize > 0)
            {
                progress?.Report(Math.Clamp(total / (double)expectedSize, 0d, 1d));
            }
        }

        progress?.Report(1d);
    }

    private static void ValidateGitHubDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Update download must use HTTPS.");
        }

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("github.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
        {
            throw new InvalidOperationException($"Blocked update download host: {uri.Host}");
        }
    }

    private string GetUpdatesDirectory(bool create)
    {
        var path = Path.Combine(_paths.Personal, "Updates");
        if (create)
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    private static void DeletePreparedUpdate(string executablePath, string manifestPath)
    {
        DeleteFileIfExists(executablePath);
        DeleteFileIfExists(manifestPath);
    }

    private static string BuildUpdaterScript()
    {
        return """
        param(
          [int]$PidToWait,
          [string]$ExePath,
          [string]$NewExePath,
          [string]$BackupPath,
          [string]$LogPath
        )
        $ErrorActionPreference = "Stop"
        function Log([string]$Message) {
          try { Add-Content -LiteralPath $LogPath -Value ("{0} {1}" -f (Get-Date -Format o), $Message) } catch {}
        }
        try {
          Log "Waiting for process $PidToWait"
          Wait-Process -Id $PidToWait -ErrorAction SilentlyContinue
          Start-Sleep -Milliseconds 700
          if (Test-Path -LiteralPath $BackupPath) { Remove-Item -LiteralPath $BackupPath -Force }
          if (Test-Path -LiteralPath $ExePath) { Move-Item -LiteralPath $ExePath -Destination $BackupPath -Force }
          Move-Item -LiteralPath $NewExePath -Destination $ExePath -Force
          Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath)
          Start-Sleep -Seconds 3
          if (Test-Path -LiteralPath $BackupPath) { Remove-Item -LiteralPath $BackupPath -Force }
          Log "Update applied"
        } catch {
          Log ("Update failed: " + $_.Exception.Message)
          try {
            if ((Test-Path -LiteralPath $BackupPath) -and -not (Test-Path -LiteralPath $ExePath)) {
              Move-Item -LiteralPath $BackupPath -Destination $ExePath -Force
            }
            Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath)
          } catch {}
        }
        """;
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string ResolveCurrentCommitSha()
    {
        var assembly = typeof(UpdateService).Assembly;
        var metadataSha = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "SourceRevisionId", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrWhiteSpace(metadataSha))
        {
            return NormalizeSha(metadataSha);
        }

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plusIndex = informational?.LastIndexOf('+') ?? -1;
        return plusIndex >= 0 && informational is not null && plusIndex + 1 < informational.Length
            ? NormalizeSha(informational[(plusIndex + 1)..])
            : string.Empty;
    }

    private static string NormalizeSha(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private static string ShortSha(string? value)
    {
        var normalized = NormalizeSha(value);
        return normalized.Length <= 7 ? normalized : normalized[..7];
    }
}

public sealed class UpdateManifest
{
    public string CommitSha { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset PublishedAtUtc { get; set; }
    public string AssetName { get; set; } = UpdateService.ExecutableAssetName;
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed record PreparedUpdate(UpdateManifest Manifest, string ExecutablePath);

public sealed class UpdateCheckResult
{
    private UpdateCheckResult(bool isUpdateAvailable, UpdateManifest? manifest, Uri? executableDownloadUrl, string message)
    {
        IsUpdateAvailable = isUpdateAvailable;
        Manifest = manifest;
        ExecutableDownloadUrl = executableDownloadUrl;
        Message = message;
    }

    public bool IsUpdateAvailable { get; }
    public UpdateManifest? Manifest { get; }
    public Uri? ExecutableDownloadUrl { get; }
    public string Message { get; }

    public static UpdateCheckResult UpToDate(string message = "") => new(false, null, null, message);

    public static UpdateCheckResult Available(UpdateManifest manifest, Uri? executableDownloadUrl) => new(true, manifest, executableDownloadUrl, "");
}

public sealed class GitHubRelease
{
    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();

    public GitHubReleaseAsset? FindAsset(string name)
    {
        return Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public Uri BrowserDownloadUrl { get; set; } = new("https://github.com/");

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
