using AutoFileMover.Core;

namespace AutoFileMover.Core.Tests;

public sealed class ContentJobNameTests : IDisposable
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "TestData", "12345ABC__STR-01.mme");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "afm-content-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;
    private readonly FakeClock _clock = new();
    private readonly MoverSettings _settings;

    public ContentJobNameTests()
    {
        _source = Directory.CreateDirectory(Path.Combine(_root, "in")).FullName;
        _target = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        _settings = new MoverSettings { SourceFolder = _source, TargetFolder = _target, SettleSeconds = 5 }; // defaults: FileContent
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private PassResult RunSettled(MovePass pass)
    {
        pass.Run();
        _clock.Advance(6);
        return pass.Run();
    }

    private MovePass NewPass() => new(_settings, new MoverLog(Path.Combine(_root, "logs")), _clock);

    [Fact]
    public void Default_pattern_reads_job_from_real_mitek_sample()
    {
        Assert.Equal("12345ABC", JobNamer.GetJobNameFromContent(Sample, JobNamer.CreateContentRegex(null)));
    }

    [Fact]
    public void File_name_rule_would_have_got_the_sample_wrong()
    {
        // Why content-based naming is the default: "12345ABC__STR-01" minus 3 chars is not the job.
        Assert.Equal("12345ABC__STR", JobNamer.GetJobName("12345ABC__STR-01.mme", 3));
    }

    [Fact]
    public void Sample_saw_file_is_moved_into_job_folder_from_its_header()
    {
        File.Copy(Sample, Path.Combine(_source, "12345ABC__STR-01.mme"));

        var result = RunSettled(NewPass());

        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(Path.Combine(_target, "12345ABC", "12345ABC__STR-01.mme")));
    }

    [Fact]
    public void Files_without_a_header_are_left_in_place_by_default()
    {
        File.WriteAllText(Path.Combine(_source, "JOB1_01.txt"), "no header here");

        var result = RunSettled(NewPass());

        Assert.Equal(0, result.Moved);
        Assert.True(File.Exists(Path.Combine(_source, "JOB1_01.txt")));
    }

    [Fact]
    public void Fallback_uses_file_name_when_header_missing()
    {
        _settings.FallbackToFileName = true;
        File.WriteAllText(Path.Combine(_source, "JOB1_01.txt"), "no header here");

        Assert.Equal(1, RunSettled(NewPass()).Moved);
        Assert.True(File.Exists(Path.Combine(_target, "JOB1", "JOB1_01.txt")));
    }

    [Fact]
    public void Binary_files_are_not_parsed()
    {
        var path = Path.Combine(_source, "x.bin");
        File.WriteAllBytes(path, [(byte)'M', (byte)'1', (byte)' ', (byte)'A', 0, 1, 2]);
        Assert.Null(JobNamer.GetJobNameFromContent(path, JobNamer.CreateContentRegex(null)));
    }

    [Fact]
    public void Custom_pattern_and_invalid_pattern()
    {
        var path = Path.Combine(_source, "a.txt");
        File.WriteAllText(path, "Header\r\nJob: ABC-77 \r\n");
        Assert.Equal("ABC-77", JobNamer.GetJobNameFromContent(path, JobNamer.CreateContentRegex(@"^Job:\s*(\S+)")));

        _settings.ContentPattern = "(unclosed";
        Assert.Contains(_settings.Validate(), e => e.Contains("content pattern"));
    }

    [Fact]
    public void Settings_store_enum_as_text()
    {
        var json = SettingsStore.Serialize(new MoverSettings { JobNameSource = JobNameSource.FileName });
        Assert.Contains("\"FileName\"", json);
    }
}
