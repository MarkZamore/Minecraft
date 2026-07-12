using System.IO;

namespace Minecraft;

public static class WorldAccessGuard
{
    public static void EnsureClosed(string worldPath)
    {
        var lockPath = Path.Combine(worldPath, "session.lock");
        if (!File.Exists(lockPath)) return;
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var lockLength = Math.Max(1, stream.Length);
            stream.Lock(0, lockLength);
            stream.Unlock(0, lockLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"World {Path.GetFileName(worldPath)} is open in Minecraft. Close the world before continuing.",
                ex);
        }
    }
}
