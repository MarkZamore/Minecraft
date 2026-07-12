using System.IO;
using System.Text.Json;

namespace Minecraft;

internal sealed class ProfileFileTransaction : IDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions ReadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly string _transactionRoot;
    private readonly string _journalPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ProfileTransactionJournal _journal;
    private readonly Dictionary<string, ProfileTransactionEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _finished;

    private ProfileFileTransaction(AppPaths paths, string operation)
    {
        _paths = paths;
        var root = Path.Combine(paths.Personal, "Temp", "ProfileTransactions");
        paths.EnsureUnderRoot(root);
        Directory.CreateDirectory(root);
        _transactionRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_transactionRoot);
        _journalPath = Path.Combine(_transactionRoot, "transaction.json");
        _journal = new ProfileTransactionJournal
        {
            SchemaVersion = SchemaVersion,
            Operation = operation,
            State = "Active",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        PersistJournal();
    }

    public static ProfileFileTransaction Begin(AppPaths paths, string operation)
    {
        return new ProfileFileTransaction(paths, operation);
    }

    public static void RecoverPending(AppPaths paths, Logger? logger)
    {
        var root = Path.Combine(paths.Personal, "Temp", "ProfileTransactions");
        if (!Directory.Exists(root)) return;
        var failures = new List<string>();

        foreach (var transactionRoot in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            try
            {
                var journalPath = Path.Combine(transactionRoot, "transaction.json");
                if (!File.Exists(journalPath))
                {
                    Directory.Delete(transactionRoot, recursive: true);
                    continue;
                }

                var journal = JsonSerializer.Deserialize<ProfileTransactionJournal>(File.ReadAllText(journalPath), ReadJsonOptions)
                    ?? throw new InvalidDataException("Profile transaction journal is empty.");
                if (journal.SchemaVersion != SchemaVersion)
                {
                    throw new InvalidDataException($"Unsupported profile transaction schema {journal.SchemaVersion}.");
                }

                if (!string.Equals(journal.State, "Committed", StringComparison.Ordinal))
                {
                    RestoreEntries(paths, transactionRoot, journal.Entries);
                    logger?.Warn($"Recovered interrupted player profile transaction {Path.GetFileName(transactionRoot)}.");
                }
                Directory.Delete(transactionRoot, recursive: true);
            }
            catch (Exception ex)
            {
                logger?.Warn($"Could not recover player profile transaction {Path.GetFileName(transactionRoot)}: {ex.Message}");
                failures.Add($"{Path.GetFileName(transactionRoot)}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Minecraft cannot continue because an interrupted player profile transaction could not be recovered:\n" +
                string.Join("\n", failures));
        }

        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
            var parent = Path.GetDirectoryName(root);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    public void Track(string path)
    {
        EnsureActive();
        var fullPath = Path.GetFullPath(path);
        _paths.EnsureUnderRoot(fullPath);
        if (_entries.ContainsKey(fullPath)) return;

        var entry = new ProfileTransactionEntry
        {
            Path = fullPath,
            Existed = File.Exists(fullPath),
            BackupFile = $"{_entries.Count:D6}.backup"
        };
        if (entry.Existed)
        {
            var backupPath = Path.Combine(_transactionRoot, entry.BackupFile);
            File.Copy(fullPath, backupPath, overwrite: false);
            File.SetAttributes(backupPath, FileAttributes.Normal);
            entry.LastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
            entry.Attributes = File.GetAttributes(fullPath);
        }

        _entries.Add(fullPath, entry);
        _journal.Entries.Add(entry);
        PersistJournal();
    }

    public void Commit()
    {
        EnsureActive();
        _journal.State = "Committed";
        PersistJournal();
        _finished = true;
        DeleteTransactionDirectory();
    }

    public void Rollback()
    {
        if (_finished) return;
        RestoreEntries(_paths, _transactionRoot, _journal.Entries);
        _finished = true;
        DeleteTransactionDirectory();
    }

    public void Dispose()
    {
        if (!_finished) Rollback();
    }

    private void PersistJournal()
    {
        AtomicFile.WriteAllText(_journalPath, JsonSerializer.Serialize(_journal, _jsonOptions));
    }

    private static void RestoreEntries(
        AppPaths paths,
        string transactionRoot,
        IReadOnlyList<ProfileTransactionEntry> entries)
    {
        List<Exception>? failures = null;
        foreach (var entry in entries.Reverse())
        {
            try
            {
                var fullPath = Path.GetFullPath(entry.Path);
                paths.EnsureUnderRoot(fullPath);
                if (entry.Existed)
                {
                    var backupPath = Path.Combine(transactionRoot, entry.BackupFile);
                    if (!File.Exists(backupPath))
                    {
                        throw new FileNotFoundException("Profile transaction backup is missing.", backupPath);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    if (File.Exists(fullPath)) File.SetAttributes(fullPath, FileAttributes.Normal);
                    File.Copy(backupPath, fullPath, overwrite: true);
                    File.SetLastWriteTimeUtc(fullPath, entry.LastWriteUtc);
                    File.SetAttributes(fullPath, entry.Attributes);
                }
                else if (File.Exists(fullPath))
                {
                    File.SetAttributes(fullPath, FileAttributes.Normal);
                    File.Delete(fullPath);
                }
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException("Player profile rollback was incomplete.", failures);
        }
    }

    private void DeleteTransactionDirectory()
    {
        if (Directory.Exists(_transactionRoot)) Directory.Delete(_transactionRoot, recursive: true);
        var root = Path.GetDirectoryName(_transactionRoot);
        if (root is not null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
        {
            Directory.Delete(root);
        }
    }

    private void EnsureActive()
    {
        if (_finished) throw new InvalidOperationException("Player profile transaction is already finished.");
    }

    private sealed class ProfileTransactionJournal
    {
        public int SchemaVersion { get; set; } = ProfileFileTransaction.SchemaVersion;
        public string Operation { get; set; } = string.Empty;
        public string State { get; set; } = "Active";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public List<ProfileTransactionEntry> Entries { get; set; } = [];
    }

    private sealed class ProfileTransactionEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool Existed { get; set; }
        public string BackupFile { get; set; } = string.Empty;
        public DateTime LastWriteUtc { get; set; }
        public FileAttributes Attributes { get; set; }
    }
}
