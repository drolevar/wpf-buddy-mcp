namespace WpfBuddy.Mcp.Server.Services;

/// <summary>
/// Shared append-with-rotation writer for the local diagnostic log files
/// (the ILogger file tee and the probe IPC log). Keeps each file under a size
/// cap and retains a few rolled backups so logs cannot grow unbounded. All
/// writes are serialized and failure-tolerant — logging must never break the server.
/// </summary>
internal static class LogFileWriter
{
    private static readonly object Gate = new();

    public static void Append(string path, string line, long maxBytes = 10L * 1024 * 1024, int retain = 3)
    {
        try
        {
            lock (Gate)
            {
                RollIfNeeded(path, maxBytes, retain);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Never let logging break the server.
        }
    }

    private static void RollIfNeeded(string path, long maxBytes, int retain)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < maxBytes)
            return;

        // Shift backups: path.(retain-1) -> path.retain, ..., path.1 -> path.2, then path -> path.1.
        for (int i = retain - 1; i >= 1; i--)
        {
            var src = $"{path}.{i}";
            if (!File.Exists(src)) continue;
            var dst = $"{path}.{i + 1}";
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }

        var first = $"{path}.1";
        if (File.Exists(first)) File.Delete(first);
        File.Move(path, first);
    }
}
