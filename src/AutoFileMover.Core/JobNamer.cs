using System.Text;
using System.Text.RegularExpressions;

namespace AutoFileMover.Core;

public enum JobNameSource
{
    /// <summary>Read the job name from inside the file (e.g. the "M1" header line of a MiTek saw file).</summary>
    FileContent,

    /// <summary>File name without extension, minus the last N characters.</summary>
    FileName,
}

public static class JobNamer
{
    /// <summary>
    /// MiTek saw files (.mme etc.) start with a header whose "M1" line holds the job name:
    /// <c>M1 12345ABC Stringer 1 0 11 ...</c> → <c>12345ABC</c>.
    /// </summary>
    public const string DefaultContentPattern = @"^M1\s+(?<job>\S+)";

    /// <summary>Only the start of the file is read — the header is in the first few lines.</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>
    /// Works out the job folder for a file: the name without its extension, minus the last
    /// <paramref name="charactersToStrip"/> characters. "JOB1234_01.saw" with 3 → "JOB1234".
    /// Returns null when no usable folder name is left (the file is then left where it is).
    /// </summary>
    public static string? GetJobName(string fileName, int charactersToStrip)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (charactersToStrip < 0 || stem.Length <= charactersToStrip)
            return null;

        return Sanitize(stem[..^charactersToStrip]);
    }

    /// <summary>
    /// Reads the start of a text file and returns the first match of <paramref name="pattern"/>
    /// (named group "job", else group 1, else the whole match). Null if not found or the file is binary.
    /// </summary>
    public static string? GetJobNameFromContent(string path, Regex pattern)
    {
        var text = ReadHeader(path);
        if (text is null)
            return null;

        try
        {
            var match = pattern.Match(text);
            if (!match.Success)
                return null;

            var group = match.Groups["job"] is { Success: true } named ? named
                : match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1]
                : match.Groups[0];
            return Sanitize(group.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Builds the regex used for content matching. Throws <see cref="ArgumentException"/> if invalid.</summary>
    public static Regex CreateContentRegex(string? pattern) =>
        new(string.IsNullOrWhiteSpace(pattern) ? DefaultContentPattern : pattern,
            RegexOptions.Multiline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

    /// <summary>Makes a string safe to use as a Windows folder name, or returns null if nothing usable is left.</summary>
    public static string? Sanitize(string raw)
    {
        // Windows folder names cannot end in a space or a dot.
        var job = raw.Trim().TrimEnd('.').Trim();
        if (job.Length == 0 || job is "." or ".." || job.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        return job;
    }

    private static string? ReadHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(MaxHeaderBytes, fs.Length)];
            var read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                return null; // binary file, no text header

            // Latin-1 maps every byte to a char, so odd bytes in the header can't break decoding.
            return Encoding.Latin1.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Applies the job-name settings (content first, optional file-name fallback) to a file.</summary>
public sealed class JobNameResolver
{
    private readonly MoverSettings _settings;
    private readonly Regex? _contentRegex;

    public JobNameResolver(MoverSettings settings)
    {
        _settings = settings;
        if (settings.JobNameSource == JobNameSource.FileContent)
            _contentRegex = JobNamer.CreateContentRegex(settings.ContentPattern);
    }

    /// <summary>Returns the job name, or null with a human-readable <paramref name="reason"/>.</summary>
    public string? Resolve(string path, out string reason)
    {
        var fileName = Path.GetFileName(path);

        if (_contentRegex is not null)
        {
            var fromContent = JobNamer.GetJobNameFromContent(path, _contentRegex);
            if (fromContent is not null)
            {
                reason = "from file contents";
                return fromContent;
            }

            if (!_settings.FallbackToFileName)
            {
                reason = "no job name found inside the file";
                return null;
            }
        }

        var fromName = JobNamer.GetJobName(fileName, _settings.CharactersToStrip);
        reason = fromName is null
            ? "name is too short to work out a job folder"
            : _contentRegex is null ? "from file name" : "from file name (not found inside the file)";
        return fromName;
    }
}
