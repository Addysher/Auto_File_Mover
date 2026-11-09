using AutoFileMover.Core;

namespace AutoFileMover.Core.Tests;

public sealed class LogFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "afm-logs-" + Guid.NewGuid().ToString("N"));
    private readonly string _default;

    public LogFolderTests() => _default = Path.Combine(_root, "default");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string Today(string dir) => MoverLog.FileFor(dir, DateTime.Now);

    [Fact]
    public void Writes_to_configured_folder_and_switches_when_changed()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        var log = new MoverLog(first, _default);

        log.Info("one");
        log.LogDirectory = second;
        log.Info("two");

        Assert.Contains("one", File.ReadAllText(Today(first)));
        Assert.Contains("two", File.ReadAllText(Today(second)));
        Assert.DoesNotContain("two", File.ReadAllText(Today(first)));
        Assert.False(File.Exists(Today(_default)));
    }

    [Fact]
    public void Blank_folder_means_default()
    {
        var log = new MoverLog(Path.Combine(_root, "x"), _default) { LogDirectory = "  " };
        Assert.Equal(_default, log.LogDirectory);
    }

    [Fact]
    public void Unwritable_folder_falls_back_to_default_and_recovers()
    {
        // A file where the folder should be makes the folder unusable.
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a folder");

        var log = new MoverLog(blocked, _default) { RetryInterval = TimeSpan.Zero };
        log.Info("kept safe");

        var fallback = File.ReadAllText(Today(_default));
        Assert.Contains("Cannot write to log folder", fallback);
        Assert.Contains("kept safe", fallback);
        Assert.Equal(_default, log.ActiveDirectory);

        // Folder becomes available again → logging returns there.
        File.Delete(blocked);
        log.Info("back home");
        Assert.Contains("back home", File.ReadAllText(Today(blocked)));
        Assert.Contains("writable again", File.ReadAllText(Today(_default)));
        Assert.Equal(blocked, log.ActiveDirectory);
    }

    [Fact]
    public void Log_folder_setting_is_validated()
    {
        var s = new MoverSettings { SourceFolder = @"C:\In", TargetFolder = @"C:\Out", LogFolder = @"relative\logs" };
        Assert.Contains(s.Validate(), e => e.Contains("log folder"));

        s.LogFolder = @"C:\Saw\Logs";
        Assert.Empty(s.Validate());
        Assert.Equal(@"C:\Saw\Logs", s.EffectiveLogFolder);

        s.LogFolder = "";
        Assert.Equal(AppPaths.LogDirectory, s.EffectiveLogFolder);
    }
}
