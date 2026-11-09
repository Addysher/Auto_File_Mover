using System.Text.Json;

namespace AutoFileMover.Core;

public enum EngineState
{
    Starting,
    Idle,
    Waiting,
    Problem,
    Paused,
    NotConfigured,
    Blocked,
    Stopped,
}

public sealed record EngineStatus(EngineState State, string Message, DateTimeOffset Updated, DateTimeOffset? LastMove = null)
{
    public static EngineStatus Now(EngineState state, string message, DateTimeOffset? lastMove = null) =>
        new(state, message, DateTimeOffset.Now, lastMove);
}

/// <summary>status.json — lets the tray app show what the service is doing.</summary>
public static class StatusFile
{
    public static void Write(string path, EngineStatus status)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Purely informational.
        }
    }

    public static EngineStatus? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<EngineStatus>(fs);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Cross-process lock so the service and the tray app never move files at the same time.
/// </summary>
public sealed class SingleEngineGuard : IDisposable
{
    private readonly FileStream _stream;

    private SingleEngineGuard(FileStream stream) => _stream = stream;

    public static SingleEngineGuard? TryAcquire(string lockFile)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
            var fs = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleEngineGuard(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
