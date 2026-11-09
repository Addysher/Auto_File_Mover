using System.Text;

namespace AutoFileMover.Core;

public enum MoverLogLevel
{
    Info,
    Warning,
    Error,
}

public sealed record MoverLogEntry(DateTimeOffset Time, MoverLogLevel Level, string Message)
{
    public override string ToString()
    {
        var tag = Level switch
        {
            MoverLogLevel.Warning => "WARN ",
            MoverLogLevel.Error => "ERROR",
            _ => "INFO ",
        };
        return $"{Time.LocalDateTime:yyyy-MM-dd HH:mm:ss} [{tag}] {Message}";
    }
}

/// <summary>
/// One plain-text log file per day: Saw_Mover-yyyy-MM-dd.log.
/// Logging never throws — a full disk must not stop files being moved.
/// </summary>
public sealed class MoverLog
{
    public const string FilePrefix = "Saw_Mover-";

    private readonly object _gate = new();
    private DateOnly _lastPurge;

    public MoverLog(string logDirectory) => LogDirectory = logDirectory;

    public string LogDirectory { get; }

    public int RetentionDays { get; set; } = 30;

    /// <summary>Raised after every entry (on the writing thread).</summary>
    public event Action<MoverLogEntry>? EntryWritten;

    public static string FileFor(string logDirectory, DateTime localDate) =>
        Path.Combine(logDirectory, $"{FilePrefix}{localDate:yyyy-MM-dd}.log");

    public string CurrentFile => FileFor(LogDirectory, DateTime.Now);

    public void Info(string message) => Write(MoverLogLevel.Info, message);

    public void Warning(string message) => Write(MoverLogLevel.Warning, message);

    public void Error(string message) => Write(MoverLogLevel.Error, message);

    public void Write(MoverLogLevel level, string message)
    {
        var entry = new MoverLogEntry(DateTimeOffset.Now, level, message);

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                using var fs = new FileStream(
                    FileFor(LogDirectory, entry.Time.LocalDateTime),
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(fs, new UTF8Encoding(false));
                writer.WriteLine(entry.ToString());
            }
            catch
            {
                // Swallow: logging must never break moving.
            }

            PurgeOldFiles(DateOnly.FromDateTime(entry.Time.LocalDateTime));
        }

        try
        {
            EntryWritten?.Invoke(entry);
        }
        catch
        {
            // A UI listener must not break the engine either.
        }
    }

    private void PurgeOldFiles(DateOnly today)
    {
        if (_lastPurge == today)
            return;
        _lastPurge = today;

        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, RetentionDays));
            foreach (var file in Directory.EnumerateFiles(LogDirectory, FilePrefix + "*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}

public static class LogReader
{
    /// <summary>Returns up to the last <paramref name="maxLines"/> lines of a log file without locking it.</summary>
    public static IReadOnlyList<string> ReadTail(string path, int maxLines, int maxBytes = 128 * 1024)
    {
        try
        {
            if (!File.Exists(path))
                return Array.Empty<string>();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipFirst = false;
            if (fs.Length > maxBytes)
            {
                fs.Seek(-maxBytes, SeekOrigin.End);
                skipFirst = true; // probably landed mid-line
            }

            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .ToList();

            if (skipFirst && lines.Count > 0)
                lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);

            return lines.Count <= maxLines ? lines : lines.GetRange(lines.Count - maxLines, maxLines);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
