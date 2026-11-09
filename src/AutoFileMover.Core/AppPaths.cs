namespace AutoFileMover.Core;

/// <summary>
/// Well-known locations shared by the Windows service and the tray app.
/// Everything lives under %ProgramData%\AutoFileMover so both can see it.
/// Set the AUTOFILEMOVER_DATA environment variable to override (handy for testing).
/// </summary>
public static class AppPaths
{
    public const string ServiceName = "AutoFileMover";
    public const string ServiceDisplayName = "Auto File Mover";

    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("AUTOFILEMOVER_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoFileMover");

    public const string SettingsFileName = "settings.json";
    public const string SweepRequestFileName = "sweep.request";

    public static string SettingsFile => Path.Combine(DataDirectory, SettingsFileName);
    public static string StatusFile => Path.Combine(DataDirectory, "status.json");
    public static string SweepRequestFile => Path.Combine(DataDirectory, SweepRequestFileName);
    public static string LockFile => Path.Combine(DataDirectory, "engine.lock");
    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");
}
