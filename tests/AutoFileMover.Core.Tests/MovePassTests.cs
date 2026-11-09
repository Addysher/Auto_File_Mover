using AutoFileMover.Core;

namespace AutoFileMover.Core.Tests;

internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
}

public sealed class MovePassTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "afm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;
    private readonly FakeClock _clock = new();
    private readonly MoverLog _log;
    private readonly MoverSettings _settings;

    public MovePassTests()
    {
        _source = Directory.CreateDirectory(Path.Combine(_root, "DataIn")).FullName;
        _target = Directory.CreateDirectory(Path.Combine(_root, "DataOut")).FullName;
        _log = new MoverLog(Path.Combine(_root, "Logs"));
        // These tests exercise the file-name rule; content-based naming is covered in ContentJobNameTests.
        _settings = new MoverSettings
        {
            SourceFolder = _source, TargetFolder = _target, SettleSeconds = 5, JobNameSource = JobNameSource.FileName,
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Drop(string name, string content = "data")
    {
        var path = Path.Combine(_source, name);
        File.WriteAllText(path, content);
        return path;
    }

    private MovePass NewPass() => new(_settings, _log, _clock);

    [Fact]
    public void New_job_files_are_grouped_into_one_folder_per_job()
    {
        Drop("JOB1234_01.saw");
        Drop("JOB1234_02.saw");
        Drop("JOB9999_01.saw");
        var pass = NewPass();

        var first = pass.Run();
        Assert.Equal(0, first.Moved);
        Assert.Equal(3, first.Pending);

        _clock.Advance(6);
        var second = pass.Run();

        Assert.Equal(3, second.Moved);
        Assert.Equal(0, second.Pending);
        Assert.Empty(Directory.GetFileSystemEntries(_source));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_target, "JOB1234")).Length);
        Assert.Single(Directory.GetFiles(Path.Combine(_target, "JOB9999")));
        Assert.False(Directory.Exists(Path.Combine(_target, MovePass.StagingFolderName)));
    }

    [Fact]
    public void Existing_job_folder_receives_file_and_overwrites_same_name()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_target, "JOB1234")).FullName;
        File.WriteAllText(Path.Combine(existing, "JOB1234_01.saw"), "old");
        Drop("JOB1234_01.saw", "new");
        var pass = NewPass();

        pass.Run();
        _clock.Advance(6);
        var result = pass.Run();

        Assert.Equal(1, result.Moved);
        Assert.Equal("new", File.ReadAllText(Path.Combine(existing, "JOB1234_01.saw")));
    }

    [Fact]
    public void File_still_being_written_is_not_moved_until_it_stops_changing()
    {
        var path = Drop("JOB1_01.saw", "a");
        var pass = NewPass();
        pass.Run();

        _clock.Advance(6);
        File.AppendAllText(path, "more"); // still growing
        var result = pass.Run();
        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Pending);
        Assert.NotNull(result.RecheckIn);

        _clock.Advance(6);
        Assert.Equal(1, pass.Run().Moved);
    }

    [Fact]
    public void Locked_file_does_not_hang_and_is_moved_once_released()
    {
        // The Python version spun forever (frozen at "-1 seconds") when a file could not be moved.
        var path = Drop("JOB1_01.saw");
        Drop("JOB2_01.saw");
        var pass = NewPass();
        pass.Run();
        _clock.Advance(6);

        PassResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = pass.Run();
        }

        Assert.Equal(1, result.Moved);   // the other job still went through
        Assert.Equal(1, result.Pending); // locked one waits
        Assert.True(File.Exists(path));

        _clock.Advance(6);
        Assert.Equal(1, pass.Run().Moved);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Subfolders_and_short_names_are_left_alone_without_looping()
    {
        Directory.CreateDirectory(Path.Combine(_source, "SomeFolder"));
        Drop("AB.saw");     // stem shorter than the 3 chars to strip → crashed the Python version
        Drop("ABC.saw");    // stem == 3 chars → nothing left
        var pass = NewPass();
        pass.Run();
        _clock.Advance(6);

        var result = pass.Run();

        Assert.Equal(0, result.Moved);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(Path.Combine(_source, "AB.saw")));
        Assert.True(Directory.Exists(Path.Combine(_source, "SomeFolder")));
        Assert.Empty(Directory.GetFileSystemEntries(_target));
    }

    [Fact]
    public void Interrupted_staging_folder_is_finished_on_next_pass()
    {
        var staging = Directory.CreateDirectory(Path.Combine(_target, MovePass.StagingFolderName, "JOB7")).FullName;
        File.WriteAllText(Path.Combine(staging, "JOB7_01.saw"), "x");

        NewPass().Run();

        Assert.True(File.Exists(Path.Combine(_target, "JOB7", "JOB7_01.saw")));
        Assert.False(Directory.Exists(Path.Combine(_target, MovePass.StagingFolderName)));
    }

    [Fact]
    public void Without_staging_files_go_straight_to_target()
    {
        _settings.StageNewFolders = false;
        Drop("JOB5_01.saw");
        var pass = NewPass();
        pass.Run();
        _clock.Advance(6);

        Assert.Equal(1, pass.Run().Moved);
        Assert.True(File.Exists(Path.Combine(_target, "JOB5", "JOB5_01.saw")));
    }

    [Fact]
    public void Missing_source_reports_problem_instead_of_throwing()
    {
        _settings.SourceFolder = Path.Combine(_root, "nope");
        var result = NewPass().Run();
        Assert.Equal(PassProblem.SourceMissing, result.Problem);
        Assert.NotNull(result.RecheckIn);
    }

    [Fact]
    public void Hidden_and_system_files_are_ignored()
    {
        var path = Drop("desktop.ini");
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
        var pass = NewPass();
        pass.Run();
        _clock.Advance(6);
        var result = pass.Run();
        Assert.Equal(0, result.Moved);
        Assert.Equal(0, result.Pending);
    }
}

public sealed class JobNamerTests
{
    [Theory]
    [InlineData("JOB1234_01.saw", 3, "JOB1234")]
    [InlineData("JOB1234-01.csv", 3, "JOB1234")]
    [InlineData("Order 55 A01.txt", 3, "Order 55")]   // trailing space trimmed
    [InlineData("noext_01", 3, "noext")]
    [InlineData("JOB1234.saw", 0, "JOB1234")]
    [InlineData("AB.saw", 3, null)]
    [InlineData("ABC.saw", 3, null)]
    [InlineData("A..01.saw", 3, "A")]                   // trailing dot trimmed
    public void Derives_job_folder(string file, int strip, string? expected) =>
        Assert.Equal(expected, JobNamer.GetJobName(file, strip));
}

public sealed class SettingsTests
{
    [Fact]
    public void Imports_legacy_python_ini()
    {
        var ini = Path.GetTempFileName();
        try
        {
            File.WriteAllText(ini, "[FOLDERS]\nsource_folder = C:/Saw/DataIn\ntarget_folder = C:/Saw/DataOut\n");
            var s = new MoverSettings();
            Assert.True(SettingsStore.TryImportLegacyIni(ini, s));
            Assert.Equal(@"C:\Saw\DataIn", s.SourceFolder);
            Assert.Equal(@"C:\Saw\DataOut", s.TargetFolder);
        }
        finally
        {
            File.Delete(ini);
        }
    }

    [Fact]
    public void Round_trips_and_validates()
    {
        var path = Path.Combine(Path.GetTempPath(), "afm-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new MoverSettings { SourceFolder = @"C:\A", TargetFolder = @"C:\B", Paused = true });
            var loaded = store.Load();
            Assert.True(loaded.Paused);
            Assert.Empty(loaded.Validate());

            Assert.NotEmpty(new MoverSettings { SourceFolder = @"C:\A", TargetFolder = @"C:\A\" }.Validate());
            Assert.NotEmpty(new MoverSettings().Validate());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
