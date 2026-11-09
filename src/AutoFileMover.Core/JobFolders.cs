namespace AutoFileMover.Core;

public sealed record JobFolderInfo(string Name, string FullPath, int FileCount, DateTime LastWriteTime);

public sealed record JobMoveResult(string Job, bool Success, bool Merged, int FilesMoved, IReadOnlyList<string> Errors);

/// <summary>Lists and moves whole job folders (used by the Jobs tab).</summary>
public static class JobFolders
{
    /// <summary>Job folders directly under <paramref name="root"/>, skipping hidden/internal ones (e.g. the staging folder).</summary>
    public static List<JobFolderInfo> List(string root)
    {
        var result = new List<JobFolderInfo>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return result;

        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
        {
            if (dir.Name.StartsWith('.') || (dir.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;

            int count;
            DateTime newest = dir.LastWriteTime;
            try
            {
                count = 0;
                foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    count++;
                    if (file.LastWriteTime > newest)
                        newest = file.LastWriteTime;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                count = -1; // unknown
            }

            result.Add(new JobFolderInfo(dir.Name, dir.FullName, count, newest));
        }

        return result;
    }

    public static bool ExistsIn(string jobPath, string destinationRoot) =>
        Directory.Exists(Path.Combine(destinationRoot, Path.GetFileName(Path.TrimEndingDirectorySeparator(jobPath))));

    /// <summary>
    /// Moves a whole job folder into <paramref name="destinationRoot"/>. Same drive and no existing folder:
    /// a single atomic rename. Otherwise files are moved one by one (copy + delete across drives), merging
    /// into an existing job folder and overwriting same-named files. Never throws for file-system errors.
    /// </summary>
    public static JobMoveResult Move(string jobPath, string destinationRoot)
    {
        jobPath = Path.TrimEndingDirectorySeparator(jobPath);
        var job = Path.GetFileName(jobPath);
        var errors = new List<string>();

        if (!Directory.Exists(jobPath))
            return new JobMoveResult(job, false, false, 0, ["the folder no longer exists"]);
        if (!Directory.Exists(destinationRoot))
            return new JobMoveResult(job, false, false, 0, [$"destination folder not found: {destinationRoot}"]);

        var destination = Path.Combine(destinationRoot, job);
        if (string.Equals(Path.GetFullPath(jobPath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return new JobMoveResult(job, false, false, 0, ["source and destination are the same folder"]);

        int total;
        try
        {
            total = Directory.EnumerateFiles(jobPath, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JobMoveResult(job, false, false, 0, [ex.Message]);
        }

        var merged = Directory.Exists(destination);
        if (!merged)
        {
            try
            {
                Directory.Move(jobPath, destination);
                return new JobMoveResult(job, true, false, total, errors);
            }
            catch (IOException)
            {
                // Different drive, or something inside is locked: fall back to file-by-file.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(jobPath, "*", SearchOption.AllDirectories).ToList())
        {
            var target = Path.Combine(destination, Path.GetRelativePath(jobPath, file));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetRelativePath(jobPath, file)}: {ex.Message}");
            }
        }

        DeleteEmptyDirectories(jobPath);
        return new JobMoveResult(job, errors.Count == 0, merged, moved, errors);
    }

    private static void DeleteEmptyDirectories(string dir)
    {
        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
                DeleteEmptyDirectories(sub);
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch
        {
            // Leave it; a later move will tidy up.
        }
    }
}
