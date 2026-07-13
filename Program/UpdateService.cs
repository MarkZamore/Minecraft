using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BsDiff;

namespace Minecraft;

public sealed class UpdateService
{
    public const string RepositoryOwner = "MarkZamore";
    public const string RepositoryName = "Minecraft";
    public const string ReleaseTag = "latest";
    public const string ExecutableAssetName = "Minecraft.exe";
    public const string ManifestAssetName = "update.json";
    public const string DeltaPatchAssetName = "Minecraft.bsdiff";
    public const int ManifestSchemaVersion = 2;
    public const string DeltaPatchAlgorithm = "bsdiff";
    public const int DeltaPatchAlgorithmVersion = 1;

    private static readonly Uri LatestReleaseApiUri = new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/tags/{ReleaseTag}");
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _currentCommitSha;
    private readonly string? _currentExecutablePath;
    private readonly TimeSpan _checkTimeout;
    private readonly VoiceNetworkCoordinator? _voiceNetwork;
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
        TimeSpan? checkTimeout = null,
        string? currentExecutablePath = null,
        VoiceNetworkCoordinator? networkCoordinator = null)
    {
        _paths = paths;
        _logger = logger;
        _httpClient = httpClient ?? PortableHttpClient.Shared;
        _currentCommitSha = string.IsNullOrWhiteSpace(currentCommitSha) ? CurrentCommitSha : NormalizeSha(currentCommitSha);
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(5);
        _currentExecutablePath = string.IsNullOrWhiteSpace(currentExecutablePath)
            ? null
            : Path.GetFullPath(currentExecutablePath);
        _voiceNetwork = networkCoordinator;
    }

    public static string CurrentCommitSha => ResolveCurrentCommitSha();
    public static int CurrentReleaseNumber => ResolveCurrentReleaseNumber();

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

            Uri? deltaPatchDownloadUrl = null;
            if (manifest.DeltaPatch is not null &&
                string.Equals(NormalizeSha(manifest.DeltaPatch.BaseCommitSha), _currentCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                var deltaAsset = release.FindAsset(manifest.DeltaPatch.AssetName);
                if (deltaAsset is null)
                {
                    _logger.Warn("Delta patch asset is missing; the full update will be downloaded.");
                }
                else if (deltaAsset.Size > 0 && deltaAsset.Size != manifest.DeltaPatch.SizeBytes)
                {
                    _logger.Warn("Delta patch asset size does not match the manifest; the full update will be downloaded.");
                }
                else if (await CurrentExecutableMatchesBaseAsync(manifest.DeltaPatch, token).ConfigureAwait(false))
                {
                    deltaPatchDownloadUrl = deltaAsset.BrowserDownloadUrl;
                }
                else
                {
                    _logger.Info("The current executable does not match the delta base; the full update will be downloaded.");
                }
            }

            return UpdateCheckResult.Available(
                manifest,
                executableAsset.BrowserDownloadUrl,
                deltaPatchDownloadUrl);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Update check failed: {ex.Message}");
            return UpdateCheckResult.UpToDate(ex.Message);
        }
    }

    public async Task<PreparedUpdate> DownloadUpdateAsync(
        UpdateCheckResult update,
        IProgress<UpdatePreparationProgress>? progress,
        CancellationToken token)
    {
        if (!update.IsUpdateAvailable || update.Manifest is null || update.ExecutableDownloadUrl is null)
        {
            throw new InvalidOperationException("No update is available.");
        }

        ValidateGitHubDownloadUri(update.ExecutableDownloadUrl);
        ValidateManifest(update.Manifest);

        if (update.DeltaPatchDownloadUrl is not null && update.Manifest.DeltaPatch is not null)
        {
            try
            {
                return await DownloadAndApplyDeltaAsync(
                    update.Manifest,
                    update.Manifest.DeltaPatch,
                    update.DeltaPatchDownloadUrl,
                    progress,
                    token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Delta update failed; downloading the full executable instead: {ex.Message}");
                CleanupDeltaArtifacts(GetUpdatesDirectory(create: false));
            }
        }

        return await DownloadFullUpdateAsync(
            update.Manifest,
            update.ExecutableDownloadUrl,
            progress,
            token).ConfigureAwait(false);
    }

    private async Task<PreparedUpdate> DownloadAndApplyDeltaAsync(
        UpdateManifest manifest,
        DeltaPatchManifest delta,
        Uri downloadUri,
        IProgress<UpdatePreparationProgress>? progress,
        CancellationToken token)
    {
        ValidateGitHubDownloadUri(downloadUri);
        var updatesDir = GetUpdatesDirectory(create: true);
        var patchPath = Path.Combine(updatesDir, $"{DeltaPatchAssetName}.download");
        var reconstructedPath = Path.Combine(updatesDir, $"{ExecutableAssetName}.new");
        CleanupDeltaArtifacts(updatesDir);

        try
        {
            await DownloadFileAsync(
                downloadUri,
                patchPath,
                delta.SizeBytes,
                delta.Sha256,
                "delta patch",
                progress,
                token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.ApplyingDelta, null));
            var currentExe = GetCurrentExecutablePath();
            await Task.Run(
                () => ApplyDeltaPatch(currentExe, patchPath, reconstructedPath, delta.BaseSha256),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            ValidateDownloadedFile(reconstructedPath, manifest);
            var readyPath = StorePreparedUpdate(reconstructedPath, manifest, updatesDir);
            DeleteFileIfExists(patchPath);
            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Ready, 1d));
            return new PreparedUpdate(manifest, readyPath);
        }
        catch
        {
            DeleteFileIfExists(patchPath);
            DeleteFileIfExists(reconstructedPath);
            throw;
        }
    }

    private async Task<PreparedUpdate> DownloadFullUpdateAsync(
        UpdateManifest manifest,
        Uri downloadUri,
        IProgress<UpdatePreparationProgress>? progress,
        CancellationToken token)
    {
        ValidateGitHubDownloadUri(downloadUri);
        var updatesDir = GetUpdatesDirectory(create: true);
        var downloadPath = Path.Combine(updatesDir, $"{ExecutableAssetName}.download");
        DeleteFileIfExists(downloadPath);
        try
        {
            await DownloadFileAsync(
                downloadUri,
                downloadPath,
                manifest.SizeBytes,
                manifest.Sha256,
                "update",
                progress,
                token).ConfigureAwait(false);

            ValidateDownloadedFile(downloadPath, manifest);
            var readyPath = StorePreparedUpdate(downloadPath, manifest, updatesDir);
            progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Ready, 1d));
            return new PreparedUpdate(manifest, readyPath);
        }
        catch
        {
            DeleteFileIfExists(downloadPath);
            throw;
        }
    }

    private async Task DownloadFileAsync(
        Uri downloadUri,
        string destinationPath,
        long expectedSize,
        string expectedSha256,
        string description,
        IProgress<UpdatePreparationProgress>? progress,
        CancellationToken token)
    {
        DeleteFileIfExists(destinationPath);
        using var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value != expectedSize)
        {
            throw new InvalidOperationException($"Downloaded {description} size does not match the manifest.");
        }

        progress?.Report(new UpdatePreparationProgress(UpdatePreparationStage.Downloading, 0d, 0, expectedSize));
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var output = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            await CopyWithProgressAsync(input, output, expectedSize, progress, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        ValidateFile(destinationPath, expectedSize, expectedSha256, description);
    }

    private string StorePreparedUpdate(string sourcePath, UpdateManifest manifest, string updatesDir)
    {
        var readyPath = Path.Combine(updatesDir, ExecutableAssetName);
        File.Move(sourcePath, readyPath, overwrite: true);
        var manifestPath = Path.Combine(updatesDir, ManifestAssetName);
        AtomicFile.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, _jsonOptions));
        return readyPath;
    }

    private static void ApplyDeltaPatch(
        string currentExecutablePath,
        string patchPath,
        string outputPath,
        string expectedBaseSha256)
    {
        var actualBaseSha256 = HashFile(currentExecutablePath);
        if (!string.Equals(actualBaseSha256, expectedBaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The current executable changed before the delta patch was applied.");
        }

        DeleteFileIfExists(outputPath);
        using var oldFile = new FileStream(
            currentExecutablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.SequentialScan);
        using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.SequentialScan | FileOptions.WriteThrough);
        BinaryPatch.Apply(oldFile, () => File.OpenRead(patchPath), output);
        output.Flush(flushToDisk: true);
    }

    private static void CleanupDeltaArtifacts(string updatesDir)
    {
        if (string.IsNullOrWhiteSpace(updatesDir)) return;
        DeleteFileIfExists(Path.Combine(updatesDir, $"{DeltaPatchAssetName}.download"));
        DeleteFileIfExists(Path.Combine(updatesDir, $"{ExecutableAssetName}.new"));
    }

    private string GetCurrentExecutablePath()
    {
        var path = _currentExecutablePath ?? Environment.ProcessPath ?? Path.Combine(_paths.Root, ExecutableAssetName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The current executable was not found.", path);
        }
        return path;
    }

    private async Task<bool> CurrentExecutableMatchesBaseAsync(DeltaPatchManifest delta, CancellationToken token)
    {
        try
        {
            var currentExe = GetCurrentExecutablePath();
            var actualSha = await Task.Run(() => HashFile(currentExe), token).ConfigureAwait(false);
            return string.Equals(actualSha, delta.BaseSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"The current executable could not be checked for a delta update: {ex.Message}");
            return false;
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

        if (manifest.ReleaseNumber < 1)
        {
            throw new InvalidOperationException("Update manifest contains an invalid release number.");
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
        ValidateFile(path, manifest.SizeBytes, manifest.Sha256, "update");
    }

    public static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != ManifestSchemaVersion)
        {
            throw new InvalidOperationException("Update manifest schema is not supported.");
        }

        if (string.IsNullOrWhiteSpace(manifest.CommitSha))
        {
            throw new InvalidOperationException("Update manifest is missing commit SHA.");
        }

        if (manifest.ReleaseNumber < 1)
        {
            throw new InvalidOperationException("Update manifest contains an invalid release number.");
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

        if (manifest.DeltaPatch is not null)
        {
            ValidateDeltaPatchManifest(manifest.DeltaPatch, manifest.CommitSha);
        }
    }

    private static void ValidateDeltaPatchManifest(DeltaPatchManifest delta, string targetCommitSha)
    {
        if (!string.Equals(delta.Algorithm, DeltaPatchAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update manifest contains an unsupported delta algorithm.");
        }
        if (delta.AlgorithmVersion != DeltaPatchAlgorithmVersion)
        {
            throw new InvalidOperationException("Update manifest contains an unsupported delta algorithm version.");
        }
        if (string.IsNullOrWhiteSpace(delta.BaseCommitSha) ||
            string.Equals(NormalizeSha(delta.BaseCommitSha), NormalizeSha(targetCommitSha), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Update manifest contains an invalid delta base commit.");
        }
        if (!string.Equals(delta.AssetName, DeltaPatchAssetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update manifest contains an unexpected delta asset name.");
        }
        if (delta.SizeBytes <= 0)
        {
            throw new InvalidOperationException("Update manifest contains an invalid delta size.");
        }
        ValidateSha256(delta.BaseSha256, "delta base");
        ValidateSha256(delta.Sha256, "delta patch");
    }

    private static void ValidateSha256(string? value, string description)
    {
        var sha256 = value?.Trim() ?? string.Empty;
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException($"Update manifest contains an invalid {description} SHA-256.");
        }
    }

    private static void ValidateFile(string path, long expectedSize, string expectedSha256, string description)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException($"Downloaded {description} was not found.", path);
        }
        if (expectedSize <= 0 || file.Length != expectedSize)
        {
            throw new InvalidOperationException($"Downloaded {description} size does not match the manifest.");
        }

        var actualSha = HashFile(path);
        if (!string.Equals(actualSha, expectedSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Downloaded {description} SHA-256 does not match the manifest.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

    private async Task CopyWithProgressAsync(
        Stream input,
        Stream output,
        long expectedSize,
        IProgress<UpdatePreparationProgress>? progress,
        CancellationToken token)
    {
        var buffer = new byte[VoiceTransferLimiter.TransferBlockSize];
        using var limiter = _voiceNetwork?.CreateTransferLimiter();
        var lastReportTimestamp = Stopwatch.GetTimestamp();
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            total += read;
            if (limiter is not null) await limiter.ThrottleAsync(read, token).ConfigureAwait(false);
            var now = Stopwatch.GetTimestamp();
            if (expectedSize > 0 &&
                (total >= expectedSize ||
                 now - lastReportTimestamp >= Stopwatch.Frequency / 10))
            {
                lastReportTimestamp = now;
                progress?.Report(new UpdatePreparationProgress(
                    UpdatePreparationStage.Downloading,
                    Math.Clamp(total / (double)expectedSize, 0d, 1d),
                    total,
                    expectedSize));
            }
        }

        progress?.Report(new UpdatePreparationProgress(
            UpdatePreparationStage.Downloading,
            1d,
            total,
            expectedSize));
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

    private static int ResolveCurrentReleaseNumber()
    {
        var value = typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, "ReleaseNumber", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return int.TryParse(value, out var releaseNumber) && releaseNumber > 0 ? releaseNumber : 1;
    }

    private static string NormalizeSha(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

}

public sealed class UpdateManifest
{
    public int SchemaVersion { get; set; }
    public string CommitSha { get; set; } = "";
    public int ReleaseNumber { get; set; }
    public string Version { get; set; } = "";
    public DateTimeOffset PublishedAtUtc { get; set; }
    public string AssetName { get; set; } = UpdateService.ExecutableAssetName;
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DeltaPatchManifest? DeltaPatch { get; set; }
}

public sealed class DeltaPatchManifest
{
    public string Algorithm { get; set; } = UpdateService.DeltaPatchAlgorithm;
    public int AlgorithmVersion { get; set; }
    public string BaseCommitSha { get; set; } = "";
    public string BaseSha256 { get; set; } = "";
    public string AssetName { get; set; } = UpdateService.DeltaPatchAssetName;
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
}

public enum UpdatePreparationStage
{
    Downloading,
    ApplyingDelta,
    Ready
}

public sealed record UpdatePreparationProgress(
    UpdatePreparationStage Stage,
    double? Fraction,
    long DownloadedBytes = 0,
    long TotalBytes = 0);

public sealed record PreparedUpdate(UpdateManifest Manifest, string ExecutablePath);

public sealed class UpdateCheckResult
{
    private UpdateCheckResult(
        bool isUpdateAvailable,
        UpdateManifest? manifest,
        Uri? executableDownloadUrl,
        Uri? deltaPatchDownloadUrl,
        string message)
    {
        IsUpdateAvailable = isUpdateAvailable;
        Manifest = manifest;
        ExecutableDownloadUrl = executableDownloadUrl;
        DeltaPatchDownloadUrl = deltaPatchDownloadUrl;
        Message = message;
    }

    public bool IsUpdateAvailable { get; }
    public UpdateManifest? Manifest { get; }
    public Uri? ExecutableDownloadUrl { get; }
    public Uri? DeltaPatchDownloadUrl { get; }
    public string Message { get; }

    public static UpdateCheckResult UpToDate(string message = "") => new(false, null, null, null, message);

    public static UpdateCheckResult Available(
        UpdateManifest manifest,
        Uri? executableDownloadUrl,
        Uri? deltaPatchDownloadUrl = null) => new(true, manifest, executableDownloadUrl, deltaPatchDownloadUrl, "");
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
