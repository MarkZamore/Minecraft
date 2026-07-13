using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using CmlLib.Core;
using CmlLib.Core.Files;
using CmlLib.Core.Installers;

namespace Minecraft;

public sealed class PortableGameInstaller : IGameInstaller
{
    private const int MaximumParallelDownloads = 4;
    private readonly HttpClient _httpClient;
    private readonly string _runtimeRoot;
    private readonly string _temporaryRoot;
    private readonly IProgress<RuntimePreparationProgress>? _runtimeProgress;
    private readonly int _phaseIndex;
    private readonly int _phaseCount;
    private readonly VoiceNetworkCoordinator? _networkCoordinator;
    private long _lastRuntimeProgressTimestamp;

    public PortableGameInstaller(
        HttpClient httpClient,
        string runtimeRoot,
        string temporaryRoot,
        IProgress<RuntimePreparationProgress>? runtimeProgress,
        int phaseIndex,
        int phaseCount,
        VoiceNetworkCoordinator? networkCoordinator = null)
    {
        _httpClient = httpClient;
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _runtimeProgress = runtimeProgress;
        _phaseIndex = phaseIndex;
        _phaseCount = phaseCount;
        _networkCoordinator = networkCoordinator;
    }

    public async ValueTask Install(
        IEnumerable<GameFile> gameFiles,
        IProgress<InstallerProgressChangedEventArgs>? fileProgress,
        IProgress<ByteProgress>? byteProgress,
        CancellationToken cancellationToken)
    {
        var files = gameFiles
            .Where(file => !string.IsNullOrWhiteSpace(file.Path))
            .GroupBy(file => Path.GetFullPath(file.Path!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        foreach (var file in files)
        {
            EnsureRuntimePath(file.Path!);
        }

        var pending = files.Where(NeedsUpdate).ToArray();
        var totalBytes = pending.Where(file => file.Size > 0).Sum(file => file.Size);
        var discoveredSizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long downloadedBytes = 0;
        var completedFiles = 0;
        using var bandwidthLimiter = _networkCoordinator?.CreateTransferLimiter();
        Interlocked.Exchange(ref _lastRuntimeProgressTimestamp, 0);
        Directory.CreateDirectory(_temporaryRoot);

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaximumParallelDownloads,
                CancellationToken = cancellationToken
            },
            async (file, token) =>
            {
                fileProgress?.Report(new InstallerProgressChangedEventArgs(
                    pending.Length,
                    Volatile.Read(ref completedFiles),
                    file.Name,
                    InstallerEventType.Queued));

                await DownloadWithRetryAsync(
                    file,
                    bytesRead =>
                    {
                        var current = Interlocked.Add(ref downloadedBytes, bytesRead);
                        ReportBytes(current, Interlocked.Read(ref totalBytes), byteProgress);
                    },
                    discoveredSize =>
                    {
                        if (file.Size > 0 || discoveredSize <= 0 ||
                            !discoveredSizes.TryAdd(file.Path!, discoveredSize)) return;
                        Interlocked.Add(ref totalBytes, discoveredSize);
                    },
                    bandwidthLimiter,
                    token);

                var completed = Interlocked.Increment(ref completedFiles);
                fileProgress?.Report(new InstallerProgressChangedEventArgs(
                    pending.Length,
                    completed,
                    file.Name,
                    InstallerEventType.Done));
            });

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await file.ExecuteUpdateTask(cancellationToken);
        }

        if (pending.Length > 0)
        {
            var finalDownloaded = Interlocked.Read(ref downloadedBytes);
            var finalTotal = Interlocked.Read(ref totalBytes);
            ReportBytes(finalDownloaded, finalTotal, byteProgress, force: true);
        }
        else
        {
            byteProgress?.Report(new ByteProgress(0, 0));
        }
        TryDeleteDirectoryIfEmpty(_temporaryRoot);
    }

    private async Task DownloadWithRetryAsync(
        GameFile file,
        Action<long> reportBytes,
        Action<long> reportDiscoveredSize,
        VoiceTransferLimiter? bandwidthLimiter,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(file.Url))
        {
            throw new InvalidDataException($"Required runtime file has no download URL: {file.Name}");
        }

        var destination = Path.GetFullPath(file.Path!);
        var tempName = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(destination))).ToLowerInvariant() + ".part";
        var temporaryPath = Path.Combine(_temporaryRoot, tempName);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            long attemptBytes = 0;
            try
            {
                DeleteFileIfExists(temporaryPath);
                var uri = NormalizeDownloadUri(file.Url);
                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 0 and var contentLength)
                {
                    reportDiscoveredSize(contentLength);
                }
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 1024 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[VoiceTransferLimiter.TransferBlockSize];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(), token);
                        if (read == 0) break;
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        if (bandwidthLimiter is not null)
                        {
                            await bandwidthLimiter.ThrottleAsync(read, token).ConfigureAwait(false);
                        }
                        attemptBytes += read;
                        reportBytes(read);
                    }
                    await output.FlushAsync(token);
                }

                ValidateFile(temporaryPath, file);
                File.Move(temporaryPath, destination, overwrite: true);
                return;
            }
            catch (OperationCanceledException)
            {
                DeleteFileIfExists(temporaryPath);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                lastError = ex;
                if (attemptBytes > 0) reportBytes(-attemptBytes);
                DeleteFileIfExists(temporaryPath);
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << (attempt - 1)), token);
                }
            }
        }

        throw new IOException($"Could not download runtime file after three attempts: {file.Name}", lastError);
    }

    private void ReportBytes(
        long downloaded,
        long total,
        IProgress<ByteProgress>? byteProgress,
        bool force = false)
    {
        var boundedDownloaded = total > 0 ? Math.Clamp(downloaded, 0, total) : Math.Max(downloaded, 0);
        byteProgress?.Report(new ByteProgress(boundedDownloaded, total));
        if (!force && !TryAcquireProgressUpdate()) return;
        double? fraction = total > 0 ? boundedDownloaded / (double)total : null;
        _runtimeProgress?.Report(new RuntimePreparationProgress(
            RuntimePreparationStage.Downloading,
            "Скачивание файлов",
            fraction,
            boundedDownloaded,
            total,
            _phaseIndex,
            _phaseCount));
    }

    private bool TryAcquireProgressUpdate()
    {
        var now = Stopwatch.GetTimestamp();
        var minimumInterval = Stopwatch.Frequency / 10;
        while (true)
        {
            var previous = Volatile.Read(ref _lastRuntimeProgressTimestamp);
            if (previous != 0 && now - previous < minimumInterval) return false;
            if (Interlocked.CompareExchange(ref _lastRuntimeProgressTimestamp, now, previous) == previous) return true;
        }
    }

    private bool NeedsUpdate(GameFile file)
    {
        var path = file.Path!;
        if (!File.Exists(path)) return true;
        var info = new FileInfo(path);
        if (file.Size > 0 && info.Length != file.Size) return true;
        if (string.IsNullOrWhiteSpace(file.Hash)) return false;
        return !string.Equals(ComputeSha1(path), file.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateFile(string path, GameFile file)
    {
        var info = new FileInfo(path);
        if (file.Size > 0 && info.Length != file.Size)
        {
            throw new InvalidDataException($"Runtime file size mismatch: {file.Name}");
        }
        if (!string.IsNullOrWhiteSpace(file.Hash) &&
            !string.Equals(ComputeSha1(path), file.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Runtime file checksum mismatch: {file.Name}");
        }
    }

    [SuppressMessage("Security", "CA5350", Justification = "SHA-1 is required to verify Mojang and loader artifacts from official metadata.")]
    private static string ComputeSha1(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private void EnsureRuntimePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = _runtimeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Runtime file escapes its isolated directory: {path}");
        }
    }

    private static Uri NormalizeDownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException($"Runtime download URL is invalid: {value}");
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
            uri = builder.Uri;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"Runtime download must use HTTPS: {uri}");
        }

        return uri;
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
