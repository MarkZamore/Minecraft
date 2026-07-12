using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minecraft;

public sealed class PackHashService : IDisposable
{
    private const int CacheSchemaVersion = 2;
    private static readonly string[] IncludedRoots =
    {
        "mods",
        "config",
        "kubejs",
        "scripts",
        "defaultconfigs",
        "resourcepacks",
        "data",
        "patchouli_books"
    };

    private static readonly string[] IncludedFiles = { PackManifestService.ManifestFileName, "options.txt" };
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public PackHashService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<string> CalculateAsync(string clientDir, CancellationToken token = default)
    {
        if (!Directory.Exists(clientDir)) return "missing";
        _paths.EnsureUnderRoot(clientDir);

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CalculateCore(clientDir, token), token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CalculateCore(string clientDir, CancellationToken token)
    {
        var cache = ReadCache();
        var packKey = Path.GetRelativePath(_paths.Packs, clientDir).Replace('\\', '/').ToLowerInvariant();
        cache.Packs.TryGetValue(packKey, out var previousPack);
        var currentFiles = new Dictionary<string, CachedPackFile>(StringComparer.OrdinalIgnoreCase);
        var records = new List<string>();

        foreach (var file in EnumerateIncludedFiles(clientDir))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(clientDir, file).Replace('\\', '/').ToLowerInvariant();
            var cacheKey = HashBytes(Encoding.UTF8.GetBytes(relative));
            var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            string sha256;
            if (previousPack?.Files.TryGetValue(cacheKey, out var cached) == true &&
                cached.SizeBytes == info.Length &&
                cached.LastWriteUtcTicks == lastWriteTicks &&
                !string.IsNullOrWhiteSpace(cached.Sha256))
            {
                sha256 = cached.Sha256;
            }
            else
            {
                sha256 = HashFile(file);
            }

            currentFiles[cacheKey] = new CachedPackFile
            {
                SizeBytes = info.Length,
                LastWriteUtcTicks = lastWriteTicks,
                Sha256 = sha256
            };
            records.Add($"{relative}|{sha256}");
        }

        records.Sort(StringComparer.OrdinalIgnoreCase);
        var hash = HashBytes(Encoding.UTF8.GetBytes(string.Join('\n', records)));
        cache.Packs[packKey] = new CachedPack { Hash = hash, Files = currentFiles };
        WriteCache(cache);
        return hash;
    }

    private IEnumerable<string> EnumerateIncludedFiles(string clientDir)
    {
        foreach (var root in IncludedRoots)
        {
            var directory = Path.Combine(clientDir, root);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!ShouldSkip(file)) yield return file;
            }
        }

        foreach (var fileName in IncludedFiles)
        {
            var file = Path.Combine(clientDir, fileName);
            if (File.Exists(file)) yield return file;
        }
    }

    private PackHashCache ReadCache()
    {
        try
        {
            if (!File.Exists(_paths.PackHashesFile)) return new PackHashCache();
            var cache = JsonSerializer.Deserialize<PackHashCache>(File.ReadAllText(_paths.PackHashesFile), _jsonOptions);
            return cache?.SchemaVersion == CacheSchemaVersion ? cache : new PackHashCache();
        }
        catch
        {
            return new PackHashCache();
        }
    }

    private void WriteCache(PackHashCache cache)
    {
        cache.SchemaVersion = CacheSchemaVersion;
        AtomicFile.WriteAllText(_paths.PackHashesFile, JsonSerializer.Serialize(cache, _jsonOptions));
    }

    private static bool ShouldSkip(string file)
    {
        var normalized = file.Replace('\\', '/');
        return normalized.Contains("/logs/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/saves/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/screenshots/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/crash-reports/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/local/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/xaero/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/search_index/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class PackHashCache
    {
        public int SchemaVersion { get; set; } = CacheSchemaVersion;
        public Dictionary<string, CachedPack> Packs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CachedPack
    {
        public string Hash { get; set; } = "";
        public Dictionary<string, CachedPackFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CachedPackFile
    {
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
