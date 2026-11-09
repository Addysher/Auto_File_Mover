namespace AutoFileMover.Core;

public enum PassProblem
{
    None,
    SourceMissing,
    TargetMissing,
}

public readonly record struct PassResult(int Moved, int Pending, int Failed, TimeSpan? RecheckIn, PassProblem Problem);

/// <summary>
/// One scan of the source folder. Each call looks at every file exactly once and returns —
/// it can never loop forever, no matter what is stuck in the folder (this was the bug in the
/// Python version: a locked file or leftover sub-folder kept its while-loop spinning on the UI thread).
/// The instance remembers file sizes between calls to know when a file has finished being written.
/// </summary>
public sealed class MovePass
{
    public const string StagingFolderName = ".automover-staging";

    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000; // OneDrive online-only
    private static readonly TimeSpan ProblemRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(30);

    private readonly MoverSettings _settings;
    private readonly MoverLog _log;
    private readonly TimeProvider _clock;

    private readonly Dictionary<string, Observation> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unnamedWarned = new(StringComparer.OrdinalIgnoreCase);
    private PassProblem _lastProblem;

    public MovePass(MoverSettings settings, MoverLog log, TimeProvider? clock = null)
    {
        _settings = settings;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _jobNames = new JobNameResolver(settings);
    }

    private readonly JobNameResolver _jobNames;

    private sealed record Observation(long Length, DateTime LastWriteUtc, DateTimeOffset StableSince);

    public PassResult Run(CancellationToken ct = default)
    {
        if (!Directory.Exists(_settings.SourceFolder))
            return Problem(PassProblem.SourceMissing, $"Source folder not found: {_settings.SourceFolder}{ServiceHint()}");
        if (!Directory.Exists(_settings.TargetFolder))
            return Problem(PassProblem.TargetMissing, $"Target folder not found: {_settings.TargetFolder}{ServiceHint()}");

        if (_lastProblem != PassProblem.None)
        {
            _log.Info("Source and target folders are reachable again.");
            _lastProblem = PassProblem.None;
        }

        var failed = RecoverStaging() ? 0 : 1;

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(_settings.SourceFolder).GetFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"Could not list the source folder: {ex.Message}");
            return new PassResult(0, 0, 1, ProblemRetry, PassProblem.None);
        }

        var settle = TimeSpan.FromSeconds(Math.Max(0, _settings.SettleSeconds));
        var now = _clock.GetUtcNow();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ready = new List<FileInfo>();
        var pending = 0;
        TimeSpan? recheck = null;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // desktop.ini, Thumbs.db and friends.
            if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;

            present.Add(file.FullName);

            if (!_seen.TryGetValue(file.FullName, out var seen)
                || seen.Length != file.Length
                || seen.LastWriteUtc != file.LastWriteTimeUtc)
            {
                seen = new Observation(file.Length, file.LastWriteTimeUtc, now);
                _seen[file.FullName] = seen;
            }

            var age = now - seen.StableSince;
            if (age < settle)
            {
                pending++;
                recheck = Min(recheck, settle - age);
                continue;
            }

            if (!IsWriteComplete(file))
            {
                pending++;
                recheck = Min(recheck, settle > TimeSpan.Zero ? settle : TimeSpan.FromSeconds(1));
                continue;
            }

            ready.Add(file);
        }

        Forget(_seen, present);
        Forget(_failures, present);
        _unnamedWarned.IntersectWith(present);

        var moved = 0;
        var groups = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ready)
        {
            var job = _jobNames.Resolve(file.FullName, out var reason);
            if (job is null)
            {
                if (_unnamedWarned.Add(file.FullName))
                    _log.Warning($"Left in place: \"{file.Name}\" - {reason}.");
                continue;
            }

            if (!groups.TryGetValue(job, out var list))
                groups[job] = list = new List<FileInfo>();
            list.Add(file);
        }

        foreach (var (job, jobFiles) in groups)
        {
            ct.ThrowIfCancellationRequested();
            var (m, f) = MoveGroup(job, jobFiles);
            moved += m;
            failed += f;
        }

        if (failed > 0)
            recheck = Min(recheck, FailureRetry);

        return new PassResult(moved, pending, failed, recheck, PassProblem.None);
    }

    private (int Moved, int Failed) MoveGroup(string job, List<FileInfo> files)
    {
        var destination = Path.Combine(_settings.TargetFolder, job);

        if (Directory.Exists(destination) || !_settings.StageNewFolders)
        {
            try
            {
                Directory.CreateDirectory(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"Could not create job folder \"{destination}\": {ex.Message}");
                return (0, files.Count);
            }

            return MoveFiles(files, destination, job);
        }

        // New job: collect its files in a hidden staging folder, then publish the folder in one go,
        // so anything watching the target never sees a half-filled job folder.
        string staging;
        try
        {
            staging = Path.Combine(EnsureStagingRoot(), job);
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not create staging folder in \"{_settings.TargetFolder}\": {ex.Message}");
            return (0, files.Count);
        }

        var (moved, failed) = MoveFiles(files, staging, job);
        if (!Publish(staging, destination, job))
            failed++;
        TryDeleteEmptyStagingRoot();
        return (moved, failed);
    }

    private (int Moved, int Failed) MoveFiles(List<FileInfo> files, string directory, string job)
    {
        int moved = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                File.Move(file.FullName, Path.Combine(directory, file.Name), overwrite: true);
                _seen.Remove(file.FullName);
                _failures.Remove(file.FullName);
                _log.Info($"Moved {file.Name} -> {job}\\");
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                var count = _failures.TryGetValue(file.FullName, out var c) ? c + 1 : 1;
                _failures[file.FullName] = count;
                if (count == 1 || count % 20 == 0)
                    _log.Warning($"Could not move {file.Name} (attempt {count}): {ex.Message} Will keep retrying.");
            }
        }

        return (moved, failed);
    }

    /// <summary>Moves a finished staging folder to its real place. Returns false if it has to be retried.</summary>
    private bool Publish(string staging, string destination, string job)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(staging).Any())
            {
                Directory.Delete(staging);
                return true;
            }

            if (!Directory.Exists(destination))
            {
                try
                {
                    Directory.Move(staging, destination);
                    _log.Info($"Published new job folder {job}\\");
                    return true;
                }
                catch (IOException)
                {
                    // Folder appeared in the meantime, or a file inside is briefly locked
                    // (e.g. antivirus). Fall back to merging file by file.
                }
            }

            Directory.CreateDirectory(destination);
            var ok = true;
            foreach (var file in Directory.GetFiles(staging))
            {
                try
                {
                    File.Move(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ok = false;
                    _log.Warning($"Could not move {Path.GetFileName(file)} out of staging into {job}\\: {ex.Message} Will retry.");
                }
            }

            if (ok)
            {
                Directory.Delete(staging, recursive: true);
                _log.Info($"Merged files into job folder {job}\\");
            }

            return ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"Could not publish job folder {job}\\: {ex.Message} Will retry.");
            return false;
        }
    }

    /// <summary>Finishes any staging folders left behind by a crash or power cut.</summary>
    private bool RecoverStaging()
    {
        var root = Path.Combine(_settings.TargetFolder, StagingFolderName);
        if (!Directory.Exists(root))
            return true;

        var ok = true;
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var job = Path.GetFileName(dir);
                _log.Info($"Finishing interrupted job folder {job}\\");
                ok &= Publish(dir, Path.Combine(_settings.TargetFolder, job), job);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning($"Could not check staging folder: {ex.Message}");
            ok = false;
        }

        TryDeleteEmptyStagingRoot();
        return ok;
    }

    private string EnsureStagingRoot()
    {
        var root = Path.Combine(_settings.TargetFolder, StagingFolderName);
        var info = Directory.CreateDirectory(root);
        if ((info.Attributes & FileAttributes.Hidden) == 0)
        {
            try
            {
                info.Attributes |= FileAttributes.Hidden;
            }
            catch
            {
                // Cosmetic only.
            }
        }

        return root;
    }

    private void TryDeleteEmptyStagingRoot()
    {
        try
        {
            var root = Path.Combine(_settings.TargetFolder, StagingFolderName);
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch
        {
            // Will be tidied next pass.
        }
    }

    /// <summary>True when nobody else has the file open (i.e. the saw software has finished writing it).</summary>
    private static bool IsWriteComplete(FileInfo file)
    {
        // Opening an online-only OneDrive file would force a download; nobody is writing those locally anyway.
        if ((file.Attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0)
            return true;

        try
        {
            using var _ = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false; // sharing violation -> still in use
        }
        catch (UnauthorizedAccessException)
        {
            return true; // can't tell; let the move try and report
        }
    }

    private PassResult Problem(PassProblem problem, string message)
    {
        if (_lastProblem != problem)
        {
            _log.Warning(message);
            _lastProblem = problem;
        }

        return new PassResult(0, 0, 0, ProblemRetry, problem);
    }

    private static string ServiceHint() =>
        Environment.UserInteractive
            ? ""
            : " (The service cannot see mapped drive letters or another user's OneDrive - use a local or UNC path such as \\\\server\\share\\folder.)";

    /// <summary>Drops remembered state for files that are no longer in the source folder.</summary>
    private static void Forget<TValue>(Dictionary<string, TValue> map, HashSet<string> present)
    {
        foreach (var key in map.Keys.Where(k => !present.Contains(k)).ToList())
            map.Remove(key);
    }

    private static TimeSpan? Min(TimeSpan? a, TimeSpan b) => a is null || b < a ? b : a;
}
