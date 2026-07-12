using System.IO;
using System.Text.Json;

namespace Minecraft;

public static class WorldTransferRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Recover(AppPaths paths, Logger logger)
    {
        var root = Path.Combine(paths.Personal, "Transfers");
        if (!Directory.Exists(root)) return;

        var failures = new List<string>();
        foreach (var transactionRoot in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray())
        {
            var journalPath = Path.Combine(transactionRoot, "transaction.json");
            try
            {
                if (!File.Exists(journalPath))
                {
                    DeleteDirectoryIfExists(transactionRoot);
                    continue;
                }

                var journal = JsonSerializer.Deserialize<WorldTransferJournal>(File.ReadAllText(journalPath), JsonOptions)
                    ?? throw new InvalidDataException("Transfer journal is empty.");
                ValidateJournalPaths(paths, journal);

                if (string.Equals(journal.Role, "Sender", StringComparison.Ordinal))
                {
                    RecoverSender(paths, logger, transactionRoot, journal);
                    continue;
                }

                if (string.Equals(journal.Role, "Receiver", StringComparison.Ordinal))
                {
                    if (journal.State is "Installed" or "Committed" && Directory.Exists(journal.InstalledWorldPath))
                    {
                        logger.Info($"Recovered completed received world transaction {journal.TransferId}.");
                    }
                    DeleteDirectoryIfExists(transactionRoot);
                    continue;
                }

                throw new InvalidDataException($"Unknown transfer journal role: {journal.Role}");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(transactionRoot)}: {ex.Message}");
                logger.Warn($"Could not recover transfer transaction {Path.GetFileName(transactionRoot)}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Minecraft cannot continue because a world transfer transaction could not be recovered:\n" +
                string.Join("\n", failures));
        }

        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    private static void RecoverSender(
        AppPaths paths,
        Logger logger,
        string transactionRoot,
        WorldTransferJournal journal)
    {
        if (string.Equals(journal.State, "Committed", StringComparison.Ordinal))
        {
            DeleteDirectoryIfExists(transactionRoot);
            return;
        }

        if (string.Equals(journal.State, "CommitSent", StringComparison.Ordinal))
        {
            if (!Directory.Exists(journal.EscrowPath))
            {
                throw new DirectoryNotFoundException($"Uncertain committed transfer has no escrow world: {journal.EscrowPath}");
            }
            logger.Warn($"Transfer {journal.TransferId} has an uncertain remote commit; its source world remains in escrow.");
            return;
        }

        if (Directory.Exists(journal.EscrowPath))
        {
            if (Directory.Exists(journal.SourceWorldPath))
            {
                throw new IOException("Both source and escrow worlds exist before commit; automatic recovery was stopped.");
            }
            paths.EnsureUnderRoot(journal.SourceWorldPath);
            Directory.CreateDirectory(Path.GetDirectoryName(journal.SourceWorldPath)!);
            Directory.Move(journal.EscrowPath, journal.SourceWorldPath);
        }
        DeleteDirectoryIfExists(transactionRoot);
    }

    private static void ValidateJournalPaths(AppPaths paths, WorldTransferJournal journal)
    {
        if (journal.SchemaVersion != 1 || !Guid.TryParseExact(journal.TransferId, "N", out _))
        {
            throw new InvalidDataException("Transfer journal identity or schema is invalid.");
        }
        if (!string.IsNullOrWhiteSpace(journal.SourceWorldPath)) paths.EnsureUnderRoot(journal.SourceWorldPath);
        if (!string.IsNullOrWhiteSpace(journal.EscrowPath)) paths.EnsureUnderRoot(journal.EscrowPath);
        if (!string.IsNullOrWhiteSpace(journal.InstalledWorldPath)) paths.EnsureUnderRoot(journal.InstalledWorldPath);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
