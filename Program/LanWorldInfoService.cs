using System.IO;

namespace Minecraft;

internal static class LanWorldInfoService
{
    public static string FindActiveWorldName(string? worldsRoot)
    {
        if (string.IsNullOrWhiteSpace(worldsRoot) || !Directory.Exists(worldsRoot)) return "Minecraft LAN";

        string? activeWorld;
        try
        {
            activeWorld = Directory.EnumerateDirectories(worldsRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(world => File.Exists(Path.Combine(world, "session.lock")))
                .Where(WorldAccessGuard.IsOpen)
                .OrderByDescending(GetLevelWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Minecraft LAN";
        }
        if (activeWorld is null) return "Minecraft LAN";

        var fallback = Path.GetFileName(activeWorld);
        try
        {
            var level = NbtFile.Read(Path.Combine(activeWorld, "level.dat"));
            var levelName = level.Root.GetCompound("Data")?.GetString("LevelName");
            return Sanitize(string.IsNullOrWhiteSpace(levelName) ? fallback : levelName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Sanitize(fallback);
        }
    }

    private static DateTime GetLevelWriteTimeUtc(string worldPath)
    {
        var levelPath = Path.Combine(worldPath, "level.dat");
        return File.Exists(levelPath) ? File.GetLastWriteTimeUtc(levelPath) : DateTime.MinValue;
    }

    private static string Sanitize(string? value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "Minecraft LAN" : value.Trim();
        return result
            .Replace('[', '(')
            .Replace(']', ')');
    }
}
