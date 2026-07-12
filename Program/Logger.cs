using System.IO;

namespace Minecraft;

public sealed class Logger
{
    private const long MaxLogBytes = 1024 * 1024;
    private const int RetainedTailBytes = 512 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    public Logger(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public event Action<string>? LineWritten;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_gate)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
            TrimIfNeeded();
        }
        LineWritten?.Invoke(line);
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length <= MaxLogBytes) return;

        string text;
        long offset;
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            offset = Math.Max(0, stream.Length - RetainedTailBytes);
            stream.Position = offset;
            var buffer = new byte[checked((int)(stream.Length - offset))];
            stream.ReadExactly(buffer);
            text = System.Text.Encoding.UTF8.GetString(buffer);
        }
        var firstLineBreak = text.IndexOf('\n');
        if (offset > 0 && firstLineBreak >= 0) text = text[(firstLineBreak + 1)..];
        AtomicFile.WriteAllText(_path, text);
    }
}
