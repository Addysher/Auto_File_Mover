using System.Text.Json.Serialization;

namespace AutoFileMover.Core;

/// <summary>A place job folders can be sent to from the Jobs tab (e.g. "Saw 1" → \\saw1\queue).</summary>
public sealed class SendToDestination
{
    public string Name { get; set; } = "";

    public string Path { get; set; } = "";

    /// <summary>Name if given, otherwise the folder's own name.</summary>
    [JsonIgnore]
    public string DisplayName =>
        Name.Trim().Length > 0 ? Name.Trim()
        : Path.Trim().Length > 0 ? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path.Trim())) is { Length: > 0 } leaf ? leaf : Path.Trim()
        : "Destination";

    public SendToDestination Copy() => new() { Name = Name, Path = Path };
}

public sealed class MoverSettings
{
    /// <summary>How many destinations the settings window offers (Core itself supports any number).</summary>
    public const int MaxDestinationsInUi = 2;

    /// <summary>Folder the saw software drops files into.</summary>
    public string SourceFolder { get; set; } = "";

    /// <summary>Folder that receives one sub-folder per job.</summary>
    public string TargetFolder { get; set; } = "";

    /// <summary>
    /// Where job folders can go next from the Jobs tab (e.g. a saw's queue). Optional; only the tray UI uses them.
    /// </summary>
    public List<SendToDestination> Destinations { get; set; } = [];

    /// <summary>Old single send-to folder (v2.0). Read for migration only; folded into <see cref="Destinations"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SendToFolder { get; set; }

    /// <summary>Migrates the old single folder and drops empty destination rows.</summary>
    public MoverSettings Normalize()
    {
        Destinations ??= [];
        if (!string.IsNullOrWhiteSpace(SendToFolder) &&
            !Destinations.Any(d => string.Equals(d.Path.Trim(), SendToFolder.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            Destinations.Insert(0, new SendToDestination { Path = SendToFolder.Trim() });
        }

        SendToFolder = null;
        Destinations.RemoveAll(d => d is null || string.IsNullOrWhiteSpace(d.Path));
        foreach (var d in Destinations)
        {
            d.Name = d.Name?.Trim() ?? "";
            d.Path = d.Path.Trim();
        }

        return this;
    }

    /// <summary>Where the job folder name comes from.</summary>
    public JobNameSource JobNameSource { get; set; } = JobNameSource.FileContent;

    /// <summary>
    /// Regex run (multi-line) over the start of the file when <see cref="JobNameSource"/> is FileContent.
    /// The named group "job" (or group 1) is the job name. Default reads the MiTek "M1" header line.
    /// </summary>
    public string ContentPattern { get; set; } = JobNamer.DefaultContentPattern;

    /// <summary>If the job name is not found inside the file, use the file-name rule instead of leaving the file.</summary>
    public bool FallbackToFileName { get; set; }

    /// <summary>
    /// File-name rule: how many characters to cut off the end of the file name (before the extension)
    /// to get the job folder name. "JOB1234_01.saw" with 3 → "JOB1234".
    /// </summary>
    public int CharactersToStrip { get; set; } = 3;

    /// <summary>A file must be unchanged (and not locked) for this long before it is moved.</summary>
    public int SettleSeconds { get; set; } = 5;

    /// <summary>Full re-scan interval, as a safety net in case a file-system event is missed.</summary>
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// When a job folder does not exist yet, build it in a hidden staging folder inside the
    /// target and publish it in one step once all its files are in (same as the original script).
    /// </summary>
    public bool StageNewFolders { get; set; } = true;

    public bool Paused { get; set; }

    public int LogRetentionDays { get; set; } = 30;

    public MoverSettings Clone()
    {
        var copy = (MoverSettings)MemberwiseClone();
        copy.Destinations = (Destinations ?? []).Select(d => d.Copy()).ToList();
        return copy;
    }

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(SourceFolder))
            errors.Add("source folder is not set");
        else if (!Path.IsPathFullyQualified(SourceFolder))
            errors.Add("source folder must be a full path (e.g. C:\\Saw\\DataIn or \\\\server\\share\\DataIn)");

        if (string.IsNullOrWhiteSpace(TargetFolder))
            errors.Add("target folder is not set");
        else if (!Path.IsPathFullyQualified(TargetFolder))
            errors.Add("target folder must be a full path");

        if (errors.Count == 0 && PathsEqual(SourceFolder, TargetFolder))
            errors.Add("source and target folders must be different");

        var destinations = (Destinations ?? []).Where(d => !string.IsNullOrWhiteSpace(d.Path)).ToList();
        for (var i = 0; i < destinations.Count; i++)
        {
            var d = destinations[i];
            var label = $"destination {i + 1} ({d.DisplayName})";
            if (!Path.IsPathFullyQualified(d.Path))
                errors.Add($"{label} must be a full path");
            else if (PathsEqual(d.Path, TargetFolder) || PathsEqual(d.Path, SourceFolder))
                errors.Add($"{label} must be different from the source and target folders");
            else if (destinations.Take(i).Any(o => PathsEqual(o.Path, d.Path)))
                errors.Add($"{label} uses the same folder as another destination");
            else if (destinations.Take(i).Any(o => string.Equals(o.DisplayName, d.DisplayName, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"{label} needs a different name from the other destination");
        }

        if (JobNameSource == JobNameSource.FileContent)
        {
            try
            {
                JobNamer.CreateContentRegex(ContentPattern);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"content pattern is not a valid regular expression ({ex.Message})");
            }
        }

        if (CharactersToStrip is < 0 or > 100)
            errors.Add("characters to strip must be between 0 and 100");
        if (SettleSeconds is < 1 or > 3600)
            errors.Add("settle time must be between 1 and 3600 seconds");
        if (SweepIntervalSeconds is < 10 or > 86400)
            errors.Add("sweep interval must be between 10 and 86400 seconds");
        if (LogRetentionDays is < 1 or > 3650)
            errors.Add("log retention must be between 1 and 3650 days");

        return errors;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
