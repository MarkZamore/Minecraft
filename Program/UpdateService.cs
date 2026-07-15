using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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
    public const int MaximumDeltaPatches = 2;
    public const string InstallJournalFileName = "install-journal.json";
    public const string InstallScriptFileName = "apply-update.ps1";
    public const string InstallCandidateFileName = "Minecraft.exe.candidate";
    public const string InstallBackupFileName = "Minecraft.exe.bak";
    public const string InstallRestartRequestFileName = "restart-requested";

    private const int UpdateCheckAttempts = 3;
    private const string PreparedDirectoryName = "Ready";
    private static readonly JsonSerializerOptions InstallJournalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
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
        _checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(20);
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
        var readyDirectory = Path.Combine(updatesDir, PreparedDirectoryName);
        RecoverPreparedDirectory(updatesDir, readyDirectory);
        var prepared = TryGetPreparedUpdateFromDirectory(readyDirectory);
        if (prepared is not null)
        {
            CleanupPreparedStaging(updatesDir);
            return prepared;
        }

        DeleteDirectoryIfExists(readyDirectory);
        RecoverPreparedDirectory(updatesDir, readyDirectory);
        prepared = TryGetPreparedUpdateFromDirectory(readyDirectory);
        if (prepared is not null)
        {
            CleanupPreparedStaging(updatesDir);
            return prepared;
        }
        return TryGetPreparedUpdateFromDirectory(updatesDir);
    }

    private void RecoverPreparedDirectory(string updatesDir, string readyDirectory)
    {
        if (Directory.Exists(readyDirectory) || !Directory.Exists(updatesDir)) return;
        var candidates = Directory.EnumerateDirectories(updatesDir, ".ready-*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(updatesDir, ".previous-*", SearchOption.TopDirectoryOnly))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .ToArray();
        foreach (var candidate in candidates)
        {
            if (TryGetPreparedUpdateFromDirectory(candidate) is null)
            {
                DeleteDirectoryIfExists(candidate);
                continue;
            }
            Directory.Move(candidate, readyDirectory);
            return;
        }
    }

    private static void CleanupPreparedStaging(string updatesDir)
    {
        if (!Directory.Exists(updatesDir)) return;
        foreach (var directory in Directory.EnumerateDirectories(updatesDir, ".ready-*", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateDirectories(updatesDir, ".previous-*", SearchOption.TopDirectoryOnly)))
        {
            DeleteDirectoryIfExists(directory);
        }
    }

    private PreparedUpdate? TryGetPreparedUpdateFromDirectory(string directory)
    {
        var executablePath = Path.Combine(directory, ExecutableAssetName);
        var manifestPath = Path.Combine(directory, ManifestAssetName);
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

    public async Task<UpdateCheckResult> CheckAsync(
        CancellationToken token,
        int attempts = UpdateCheckAttempts,
        TimeSpan? attemptTimeout = null)
    {
        attempts = Math.Clamp(attempts, 1, UpdateCheckAttempts);
        var effectiveTimeout = attemptTimeout ?? _checkTimeout;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(effectiveTimeout);
                var manifest = await DownloadManifestAsync(
                    BuildReleaseAssetUri(ManifestAssetName, preventCaching: true),
                    timeoutCts.Token).ConfigureAwait(false);
                ValidateManifest(manifest);
                var evaluation = EvaluateManifest(manifest, _currentCommitSha);
                if (!evaluation.IsUpdateAvailable)
                {
                    return evaluation;
                }

                Uri? deltaPatchDownloadUrl = null;
                var deltaPatch = await FindMatchingDeltaPatchAsync(manifest, token).ConfigureAwait(false);
                if (deltaPatch is not null)
                {
                    deltaPatchDownloadUrl = BuildReleaseAssetUri(deltaPatch.AssetName);
                }

                return UpdateCheckResult.Available(
                    manifest,
                    BuildReleaseAssetUri(manifest.AssetName),
                    deltaPatch,
                    deltaPatchDownloadUrl);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.Warn($"Update check attempt {attempt}/{attempts} failed: {ex.Message}");
                if (attempt < attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false);
                }
            }
        }

        var message = lastError?.Message ?? "The update service is unavailable.";
        return UpdateCheckResult.UpToDate(message);
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

        if (update.DeltaPatchDownloadUrl is not null && update.DeltaPatch is not null)
        {
            try
            {
                return await DownloadAndApplyDeltaAsync(
                    update.Manifest,
                    update.DeltaPatch,
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
        var patchPath = Path.Combine(updatesDir, $"{delta.AssetName}.download");
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
        var candidateDirectory = Path.Combine(updatesDir, $".ready-{Guid.NewGuid():N}");
        var readyDirectory = Path.Combine(updatesDir, PreparedDirectoryName);
        var previousDirectory = Path.Combine(updatesDir, $".previous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(candidateDirectory);
        try
        {
            var candidateExecutable = Path.Combine(candidateDirectory, ExecutableAssetName);
            var candidateManifest = Path.Combine(candidateDirectory, ManifestAssetName);
            File.Move(sourcePath, candidateExecutable);
            AtomicFile.WriteAllText(candidateManifest, JsonSerializer.Serialize(manifest, _jsonOptions));
            ValidateDownloadedFile(candidateExecutable, manifest);

            var movedPrevious = false;
            if (Directory.Exists(readyDirectory))
            {
                Directory.Move(readyDirectory, previousDirectory);
                movedPrevious = true;
            }

            try
            {
                Directory.Move(candidateDirectory, readyDirectory);
            }
            catch
            {
                if (movedPrevious && !Directory.Exists(readyDirectory) && Directory.Exists(previousDirectory))
                {
                    Directory.Move(previousDirectory, readyDirectory);
                }
                throw;
            }

            DeleteFileIfExists(Path.Combine(updatesDir, ExecutableAssetName));
            DeleteFileIfExists(Path.Combine(updatesDir, ManifestAssetName));
            DeleteDirectoryIfExists(previousDirectory);
            return Path.Combine(readyDirectory, ExecutableAssetName);
        }
        finally
        {
            DeleteDirectoryIfExists(candidateDirectory);
        }
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
        if (Directory.Exists(updatesDir))
        {
            foreach (var path in Directory.EnumerateFiles(updatesDir, "*.bsdiff.download", SearchOption.TopDirectoryOnly))
            {
                DeleteFileIfExists(path);
            }
        }
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

    private async Task<DeltaPatchManifest?> FindMatchingDeltaPatchAsync(
        UpdateManifest manifest,
        CancellationToken token)
    {
        try
        {
            var candidates = manifest.DeltaPatches
                .Concat(manifest.DeltaPatch is null ? [] : [manifest.DeltaPatch])
                .Where(delta => string.Equals(
                    NormalizeSha(delta.BaseCommitSha),
                    _currentCommitSha,
                    StringComparison.OrdinalIgnoreCase))
                .DistinctBy(delta => $"{NormalizeSha(delta.BaseSha256)}|{delta.AssetName}", StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (candidates.Length == 0) return null;

            var currentExe = GetCurrentExecutablePath();
            var actualSha = await Task.Run(() => HashFile(currentExe), token).ConfigureAwait(false);
            return candidates.FirstOrDefault(delta =>
                string.Equals(actualSha, delta.BaseSha256, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"The current executable could not be checked for a delta update: {ex.Message}");
            return null;
        }
    }

    public void StartInstall(PreparedUpdate prepared, UpdateInstallMode mode)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ValidateManifest(prepared.Manifest);
        ValidateDownloadedFile(prepared.ExecutablePath, prepared.Manifest);

        var currentExe = GetCurrentExecutablePath();
        var updatesDir = GetUpdatesDirectory(create: true);
        if (IsInstallationInProgress(updatesDir))
        {
            throw new InvalidOperationException("Another update installation is already running.");
        }
        var preparedExe = Path.GetFullPath(prepared.ExecutablePath);
        EnsureUnderDirectory(preparedExe, updatesDir, "Prepared update");
        var preparedManifest = Path.Combine(Path.GetDirectoryName(preparedExe)!, ManifestAssetName);
        if (!File.Exists(preparedManifest))
        {
            throw new FileNotFoundException("Downloaded update manifest was not found.", preparedManifest);
        }

        var scriptPath = Path.Combine(updatesDir, InstallScriptFileName);
        var candidatePath = Path.Combine(updatesDir, InstallCandidateFileName);
        var backupPath = Path.Combine(updatesDir, InstallBackupFileName);
        var journalPath = Path.Combine(updatesDir, InstallJournalFileName);
        var restartRequestPath = Path.Combine(updatesDir, InstallRestartRequestFileName);
        var logPath = Path.Combine(updatesDir, "update.log");
        var currentSha256 = HashFile(currentExe);
        var startedAtUtc = DateTimeOffset.UtcNow;

        DeleteFileIfExists(candidatePath);
        DeleteFileIfExists(backupPath);
        DeleteFileIfExists(scriptPath);
        DeleteFileIfExists(restartRequestPath);
        var journal = new UpdateInstallJournal
        {
            Status = UpdateInstallStatus.Scheduled,
            SourceProcessId = Environment.ProcessId,
            TargetCommitSha = prepared.Manifest.CommitSha,
            TargetSha256 = prepared.Manifest.Sha256,
            TargetSizeBytes = prepared.Manifest.SizeBytes,
            RestartAfterInstall = mode == UpdateInstallMode.InstallAndRestart,
            StartedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc
        };
        AtomicFile.WriteAllText(journalPath, JsonSerializer.Serialize(journal, _jsonOptions));
        AtomicFile.WriteAllText(scriptPath, BuildUpdaterScript(), Encoding.UTF8);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = _paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile",
                     "-ExecutionPolicy", "Bypass",
                     "-File", scriptPath,
                     "-PidToWait", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-ExePath", currentExe,
                     "-PreparedExePath", preparedExe,
                     "-PreparedManifestPath", preparedManifest,
                     "-CandidatePath", candidatePath,
                     "-BackupPath", backupPath,
                     "-JournalPath", journalPath,
                     "-RestartRequestPath", restartRequestPath,
                     "-LogPath", logPath,
                     "-ExpectedSize", prepared.Manifest.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-ExpectedSha256", prepared.Manifest.Sha256,
                     "-CurrentSha256", currentSha256,
                     "-TargetCommitSha", prepared.Manifest.CommitSha,
                     "-RestartAfterInstall", mode == UpdateInstallMode.InstallAndRestart ? "1" : "0",
                     "-StartedAtUtc", startedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("The update helper could not be started.");
            _logger.Info($"Update installation scheduled ({mode}).");
        }
        catch (Exception ex)
        {
            journal.Status = UpdateInstallStatus.Failed;
            journal.Error = ex.Message;
            journal.UpdatedAtUtc = DateTimeOffset.UtcNow;
            AtomicFile.WriteAllText(journalPath, JsonSerializer.Serialize(journal, _jsonOptions));
            throw;
        }
    }

    public static bool IsInstallationInProgress(string updatesDirectory)
    {
        var journalPath = Path.Combine(updatesDirectory, InstallJournalFileName);
        if (!File.Exists(journalPath)) return false;
        try
        {
            var journal = JsonSerializer.Deserialize<UpdateInstallJournal>(
                File.ReadAllText(journalPath),
                InstallJournalJsonOptions);
            if (journal is null || journal.Status is not (UpdateInstallStatus.Scheduled or UpdateInstallStatus.Applying))
            {
                return false;
            }

            if (journal.HelperProcessId > 0)
            {
                try
                {
                    using var process = Process.GetProcessById(journal.HelperProcessId);
                    return !process.HasExited;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            return DateTimeOffset.UtcNow - journal.UpdatedAtUtc < TimeSpan.FromMinutes(2);
        }
        catch
        {
            return false;
        }
    }

    public bool RequestRestartForActiveInstallation()
    {
        var updatesDirectory = GetUpdatesDirectory(create: false);
        if (!IsInstallationInProgress(updatesDirectory)) return false;

        AtomicFile.WriteAllText(
            Path.Combine(updatesDirectory, InstallRestartRequestFileName),
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        _logger.Info("A running update installation will open the application when it completes.");
        return true;
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
        manifest.DeltaPatches ??= [];
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
            ValidateDeltaPatchManifest(
                manifest.DeltaPatch,
                manifest.CommitSha,
                requireLegacyAssetName: true);
        }

        if (manifest.DeltaPatches.Count > MaximumDeltaPatches)
        {
            throw new InvalidOperationException("Update manifest contains too many delta patches.");
        }
        foreach (var delta in manifest.DeltaPatches)
        {
            ValidateDeltaPatchManifest(delta, manifest.CommitSha, requireLegacyAssetName: false);
        }
        if (manifest.DeltaPatches
            .GroupBy(delta => NormalizeSha(delta.BaseSha256), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("Update manifest contains duplicate delta bases.");
        }
    }

    private static void ValidateDeltaPatchManifest(
        DeltaPatchManifest delta,
        string targetCommitSha,
        bool requireLegacyAssetName)
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
        if (requireLegacyAssetName && !string.Equals(delta.AssetName, DeltaPatchAssetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Update manifest contains an unexpected delta asset name.");
        }
        if (!IsSafeDeltaAssetName(delta.AssetName))
        {
            throw new InvalidOperationException("Update manifest contains an invalid delta asset name.");
        }
        if (!requireLegacyAssetName && delta.BaseReleaseNumber < 1)
        {
            throw new InvalidOperationException("Update manifest contains an invalid delta base release number.");
        }
        if (delta.SizeBytes <= 0)
        {
            throw new InvalidOperationException("Update manifest contains an invalid delta size.");
        }
        ValidateSha256(delta.BaseSha256, "delta base");
        ValidateSha256(delta.Sha256, "delta patch");
    }

    private static bool IsSafeDeltaAssetName(string? assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName) ||
            !assetName.EndsWith(".bsdiff", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(assetName), assetName, StringComparison.Ordinal) ||
            assetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        return true;
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

    private async Task<UpdateManifest> DownloadManifestAsync(Uri manifestUri, CancellationToken token)
    {
        ValidateGitHubDownloadUri(manifestUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, _jsonOptions, token) ??
               throw new InvalidOperationException("Update manifest was empty.");
    }

    private static Uri BuildReleaseAssetUri(string assetName, bool preventCaching = false)
    {
        var uri =
            $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/download/{ReleaseTag}/{Uri.EscapeDataString(assetName)}";
        if (preventCaching)
        {
            uri += $"?cache={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        }
        return new Uri(uri, UriKind.Absolute);
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
          [string]$PreparedExePath,
          [string]$PreparedManifestPath,
          [string]$CandidatePath,
          [string]$BackupPath,
          [string]$JournalPath,
          [string]$RestartRequestPath,
          [string]$LogPath,
          [long]$ExpectedSize,
          [string]$ExpectedSha256,
          [string]$CurrentSha256,
          [string]$TargetCommitSha,
          [int]$RestartAfterInstall,
          [string]$StartedAtUtc
        )
        $ErrorActionPreference = "Stop"
        function Log([string]$Message) {
          try { Add-Content -LiteralPath $LogPath -Value ("{0} {1}" -f (Get-Date -Format o), $Message) } catch {}
        }
        function Write-Journal([string]$Status, [string]$ErrorMessage) {
          try {
            $value = [ordered]@{
              schemaVersion = 1
              status = $Status
              sourceProcessId = $PidToWait
              helperProcessId = $PID
              targetCommitSha = $TargetCommitSha
              targetSha256 = $ExpectedSha256
              targetSizeBytes = $ExpectedSize
              restartAfterInstall = ($RestartAfterInstall -eq 1)
              startedAtUtc = $StartedAtUtc
              updatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
              error = $ErrorMessage
            }
            $temporary = "$JournalPath.tmp-$PID"
            $value | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $temporary -Encoding UTF8
            Move-Item -LiteralPath $temporary -Destination $JournalPath -Force
          } catch {}
        }
        function Get-Sha256([string]$Path) {
          return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        function Assert-File([string]$Path, [long]$Size, [string]$Sha256) {
          $file = Get-Item -LiteralPath $Path -ErrorAction Stop
          if ($file.Length -ne $Size) { throw "Update file size does not match the manifest." }
          if ((Get-Sha256 $Path) -ne $Sha256.ToLowerInvariant()) { throw "Update file SHA-256 does not match the manifest." }
        }
        function Restore-PreviousExecutable {
          if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) { return }
          if (Test-Path -LiteralPath $ExePath -PathType Leaf) { Remove-Item -LiteralPath $ExePath -Force }
          Move-Item -LiteralPath $BackupPath -Destination $ExePath -Force
          if (-not [string]::IsNullOrWhiteSpace($CurrentSha256) -and
              (Get-Sha256 $ExePath) -ne $CurrentSha256.ToLowerInvariant()) {
            throw "The previous executable could not be restored safely."
          }
        }

        $replacementStarted = $false
        Write-Journal "Applying" ""
        Log "Waiting for process $PidToWait"
        Wait-Process -Id $PidToWait -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 700
        try {
          Assert-File $PreparedExePath $ExpectedSize $ExpectedSha256
          if (Test-Path -LiteralPath $CandidatePath) { Remove-Item -LiteralPath $CandidatePath -Force }
          Copy-Item -LiteralPath $PreparedExePath -Destination $CandidatePath -Force
          Assert-File $CandidatePath $ExpectedSize $ExpectedSha256
          if (Test-Path -LiteralPath $BackupPath) { Remove-Item -LiteralPath $BackupPath -Force }
          $replacementStarted = $true
          if (Test-Path -LiteralPath $ExePath -PathType Leaf) {
            [System.IO.File]::Replace($CandidatePath, $ExePath, $BackupPath, $true)
          } else {
            [System.IO.File]::Move($CandidatePath, $ExePath)
          }
          Assert-File $ExePath $ExpectedSize $ExpectedSha256
        } catch {
          $failure = $_.Exception.Message
          Log ("Update failed: " + $failure)
          if ($replacementStarted) {
            try { Restore-PreviousExecutable } catch { Log ("Rollback failed: " + $_.Exception.Message) }
          }
          if (Test-Path -LiteralPath $CandidatePath) { Remove-Item -LiteralPath $CandidatePath -Force -ErrorAction SilentlyContinue }
          Write-Journal "Failed" $failure
          $restartRequested = $RestartAfterInstall -eq 1 -or (Test-Path -LiteralPath $RestartRequestPath -PathType Leaf)
          if (Test-Path -LiteralPath $RestartRequestPath) { Remove-Item -LiteralPath $RestartRequestPath -Force -ErrorAction SilentlyContinue }
          if ($restartRequested -and (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
            try {
              Start-Process -FilePath $ExePath `
                -ArgumentList ("--skip-prestart-update=" + $TargetCommitSha) `
                -WorkingDirectory (Split-Path -Parent $ExePath)
            } catch {}
          }
          exit 1
        }

        Log "Update applied"
        if (Test-Path -LiteralPath $BackupPath) { Remove-Item -LiteralPath $BackupPath -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $PreparedExePath) { Remove-Item -LiteralPath $PreparedExePath -Force -ErrorAction SilentlyContinue }
        if (Test-Path -LiteralPath $PreparedManifestPath) { Remove-Item -LiteralPath $PreparedManifestPath -Force -ErrorAction SilentlyContinue }
        $preparedDirectory = Split-Path -Parent $PreparedExePath
        if ((Split-Path -Leaf $preparedDirectory) -eq "Ready" -and
            (Test-Path -LiteralPath $preparedDirectory) -and
            -not (Get-ChildItem -LiteralPath $preparedDirectory -Force | Select-Object -First 1)) {
          Remove-Item -LiteralPath $preparedDirectory -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $JournalPath) { Remove-Item -LiteralPath $JournalPath -Force -ErrorAction SilentlyContinue }
        $restartRequested = $RestartAfterInstall -eq 1 -or (Test-Path -LiteralPath $RestartRequestPath -PathType Leaf)
        if (Test-Path -LiteralPath $RestartRequestPath) { Remove-Item -LiteralPath $RestartRequestPath -Force -ErrorAction SilentlyContinue }
        if ($restartRequested) {
          try { Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath) } catch { Log ("Updated application could not be started: " + $_.Exception.Message) }
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

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void EnsureUnderDirectory(string path, string directory, string description)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{description} is outside the update directory.");
        }
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
    public List<DeltaPatchManifest> DeltaPatches { get; set; } = [];
}

public sealed class DeltaPatchManifest
{
    public string Algorithm { get; set; } = UpdateService.DeltaPatchAlgorithm;
    public int AlgorithmVersion { get; set; }
    public int BaseReleaseNumber { get; set; }
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

public enum UpdateInstallMode
{
    InstallOnExit,
    InstallAndRestart
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpdateInstallStatus
{
    Scheduled,
    Applying,
    Failed
}

public sealed class UpdateInstallJournal
{
    public int SchemaVersion { get; set; } = 1;
    public UpdateInstallStatus Status { get; set; }
    public int SourceProcessId { get; set; }
    public int HelperProcessId { get; set; }
    public string TargetCommitSha { get; set; } = "";
    public string TargetSha256 { get; set; } = "";
    public long TargetSizeBytes { get; set; }
    public bool RestartAfterInstall { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Error { get; set; } = "";
}

public sealed class UpdateCheckResult
{
    private UpdateCheckResult(
        bool isUpdateAvailable,
        UpdateManifest? manifest,
        Uri? executableDownloadUrl,
        DeltaPatchManifest? deltaPatch,
        Uri? deltaPatchDownloadUrl,
        string message)
    {
        IsUpdateAvailable = isUpdateAvailable;
        Manifest = manifest;
        ExecutableDownloadUrl = executableDownloadUrl;
        DeltaPatch = deltaPatch;
        DeltaPatchDownloadUrl = deltaPatchDownloadUrl;
        Message = message;
    }

    public bool IsUpdateAvailable { get; }
    public UpdateManifest? Manifest { get; }
    public Uri? ExecutableDownloadUrl { get; }
    public DeltaPatchManifest? DeltaPatch { get; }
    public Uri? DeltaPatchDownloadUrl { get; }
    public string Message { get; }

    public static UpdateCheckResult UpToDate(string message = "") => new(false, null, null, null, null, message);

    public static UpdateCheckResult Available(
        UpdateManifest manifest,
        Uri? executableDownloadUrl,
        DeltaPatchManifest? deltaPatch = null,
        Uri? deltaPatchDownloadUrl = null) =>
        new(true, manifest, executableDownloadUrl, deltaPatch, deltaPatchDownloadUrl, "");
}
