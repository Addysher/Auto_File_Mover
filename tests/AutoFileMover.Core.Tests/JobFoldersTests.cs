using AutoFileMover.Core;

namespace AutoFileMover.Core.Tests;

public sealed class JobFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "afm-jobs-" + Guid.NewGuid().ToString("N"));
    private readonly string _from;
    private readonly string _to;

    public JobFoldersTests()
    {
        _from = Directory.CreateDirectory(Path.Combine(_root, "DataOut")).FullName;
        _to = Directory.CreateDirectory(Path.Combine(_root, "Queue")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeJob(string root, string job, params string[] files)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, job)).FullName;
        foreach (var f in files)
        {
            var path = Path.Combine(dir, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{root}:{f}");
        }

        return dir;
    }

    [Fact]
    public void Lists_jobs_with_file_counts_and_skips_staging_folder()
    {
        MakeJob(_from, "12345ABC", "a.mme", "b.mme", @"sub\c.mme");
        MakeJob(_from, "60001ABC", "a.mme");
        Directory.CreateDirectory(Path.Combine(_from, MovePass.StagingFolderName, "X"));

        var jobs = JobFolders.List(_from).OrderBy(j => j.Name).ToList();

        Assert.Equal(["12345ABC", "60001ABC"], jobs.Select(j => j.Name));
        Assert.Equal(3, jobs[0].FileCount);
        Assert.Empty(JobFolders.List(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Moves_whole_folder_including_subfolders()
    {
        var job = MakeJob(_from, "12345ABC", "a.mme", @"sub\c.mme");

        var r = JobFolders.Move(job, _to);

        Assert.True(r.Success);
        Assert.False(r.Merged);
        Assert.Equal(2, r.FilesMoved);
        Assert.False(Directory.Exists(job));
        Assert.True(File.Exists(Path.Combine(_to, "12345ABC", "sub", "c.mme")));
    }

    [Fact]
    public void Merges_into_existing_job_and_overwrites_same_names()
    {
        var job = MakeJob(_from, "12345ABC", "a.mme", "new.mme");
        MakeJob(_to, "12345ABC", "a.mme", "old.mme");

        var r = JobFolders.Move(job, _to);

        Assert.True(r.Success);
        Assert.True(r.Merged);
        var dest = Path.Combine(_to, "12345ABC");
        Assert.Equal($"{_from}:a.mme", File.ReadAllText(Path.Combine(dest, "a.mme")));
        Assert.True(File.Exists(Path.Combine(dest, "old.mme")));
        Assert.True(File.Exists(Path.Combine(dest, "new.mme")));
        Assert.False(Directory.Exists(job));
    }

    [Fact]
    public void Locked_file_is_reported_and_left_behind_without_losing_anything()
    {
        var job = MakeJob(_from, "12345ABC", "a.mme", "locked.mme");

        JobMoveResult r;
        using (new FileStream(Path.Combine(job, "locked.mme"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r = JobFolders.Move(job, _to);
        }

        Assert.False(r.Success);
        Assert.Single(r.Errors);
        Assert.True(File.Exists(Path.Combine(job, "locked.mme")));
        Assert.True(File.Exists(Path.Combine(_to, "12345ABC", "a.mme")));

        // Second attempt merges the remainder.
        var again = JobFolders.Move(job, _to);
        Assert.True(again.Success);
        Assert.False(Directory.Exists(job));
    }

    [Fact]
    public void Missing_destination_fails_cleanly()
    {
        var job = MakeJob(_from, "J1", "a.mme");
        var r = JobFolders.Move(job, Path.Combine(_root, "nope"));
        Assert.False(r.Success);
        Assert.True(Directory.Exists(job));
    }
}
