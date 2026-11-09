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
    private string _logDirectory;
    private DateOnly _lastPurge;
    private string? _failedDirectory;
    private DateTime _retryAtUtc;

    /// <summary>How long to wait before trying an unavailable log folder again.</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <param name="logDirectory">Folder to write to (can be changed later, e.g. from settings).</param>
    /// <param name="fallbackDirectory">Used when <paramref name="logDirectory"/> can't be written. Defaults to %ProgramData%\AutoFileMover\Logs.</param>
    public MoverLog(string logDirectory, string? fallbackDirectory = null)
    {
        _logDirectory = logDirectory;
        FallbackDirectory = fallbackDirectory ?? AppPaths.LogDirectory;
    }

    /// <summary>The configured log folder. Changing it takes effect from the next line written.</summary>
    public string LogDirectory
    {
        get
        {
            lock (_gate)
                return _logDirectory;
        }
        set
        {
            var folder = string.IsNullOrWhiteSpace(value) ? FallbackDirectory : value.Trim();
            lock (_gate)
            {
                if (string.Equals(folder, _logDirectory, StringComparison.OrdinalIgnoreCase))
                    return;
                _logDirectory = folder;
                _lastPurge = default;
                _failedDirectory = null;
                _retryAtUtc = default;
            }
        }
    }

    public string FallbackDirectory { get; }

    /// <summary>Where the last line actually went (the fallback folder if the configured one failed).</summary>
    public string ActiveDirectory
    {
        get
        {
            lock (_gate)
                return _failedDirectory is null ? _logDirectory : FallbackDirectory;
        }
    }

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
            var line = entry.ToString();
            var date = entry.Time.LocalDateTime;

            string? error = null;
            var tryConfigured = _failedDirectory is null || DateTime.UtcNow >= _retryAtUtc;
            if (tryConfigured && TryAppend(_logDirectory, date, line, out error))
            {
                if (_failedDirectory is not null)
                {
                    TryAppend(FallbackDirectory, date, $"{line[..19]} [INFO ] Log folder \"{_logDirectory}\" is writable again; logging there.", out _);
                    _failedDirectory = null;
                }
            }
            else if (!string.Equals(_logDirectory, FallbackDirectory, StringComparison.OrdinalIgnoreCase))
            {
                // Configured folder unavailable (drive missing, no permission…): keep the line in the default folder,
                // and only re-try the configured folder once a minute (an offline share can take seconds to time out).
                if (tryConfigured)
                    _retryAtUtc = DateTime.UtcNow + RetryInterval;
                if (_failedDirectory is null)
                {
                    _failedDirectory = _logDirectory;
                    TryAppend(FallbackDirectory, date,
                        $"{line[..19]} [WARN ] Cannot write to log folder \"{_logDirectory}\" ({error}). Logging here until it is available.", out _);
                }

                TryAppend(FallbackDirectory, date, line, out _);
            }

            PurgeOldFiles(DateOnly.FromDateTime(date));
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

    private static bool TryAppend(string directory, DateTime date, string line, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var fs = new FileStream(FileFor(directory, date), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(fs, new UTF8Encoding(false));
            writer.WriteLine(line);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // Never throw: logging must not stop files being moved.
            error = ex.Message;
            return false;
        }
    }

    private void PurgeOldFiles(DateOnly today)
    {
        if (_lastPurge == today)
            return;
        _lastPurge = today;

        try
        {
            // Only our own Saw_Mover-*.log files are ever deleted, even in a shared folder.
            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, RetentionDays));
            foreach (var file in Directory.EnumerateFiles(ActiveDirectoryUnlocked(), FilePrefix + "*.log"))
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

    private string ActiveDirectoryUnlocked() => _failedDirectory is null ? _logDirectory : FallbackDirectory;
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
