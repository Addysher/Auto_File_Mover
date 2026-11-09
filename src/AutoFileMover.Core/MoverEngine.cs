namespace AutoFileMover.Core;

/// <summary>
/// Runs <see cref="MovePass"/> whenever the source folder changes, when a file is due to have
/// settled, and on a periodic sweep. Exceptions are logged and the loop carries on — it never
/// just stops the way the old countdown did.
/// </summary>
public sealed class MoverEngine
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProblemRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(250);

    private readonly MoverSettings _settings;
    private readonly MoverLog _log;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _kick = new(0, 1);

    private FileSystemWatcher? _watcher;
    private volatile bool _watcherBroken;
    private DateTimeOffset? _lastMove;

    public MoverEngine(MoverSettings settings, MoverLog log, TimeProvider? clock = null)
    {
        _settings = settings.Clone();
        _log = log;
        _clock = clock ?? TimeProvider.System;
        Status = EngineStatus.Now(EngineState.Starting, "Starting");
    }

    public EngineStatus Status { get; private set; }

    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Ask for an immediate scan (e.g. the "Run now" button).</summary>
    public void RequestSweep() => Kick();

    public async Task RunAsync(CancellationToken ct)
    {
        var pass = new MovePass(_settings, _log, _clock);
        _log.Info($"Watching \"{_settings.SourceFolder}\" -> \"{_settings.TargetFolder}\" " +
                  $"(strip {_settings.CharactersToStrip} chars, settle {_settings.SettleSeconds}s, sweep every {_settings.SweepIntervalSeconds}s)");

        var wait = TimeSpan.Zero;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (wait > TimeSpan.Zero)
                {
                    var kicked = await _kick.WaitAsync(wait, ct).ConfigureAwait(false);
                    if (kicked)
                        await Task.Delay(Debounce, ct).ConfigureAwait(false); // let a burst of events settle
                    while (_kick.Wait(0))
                    {
                    }
                }

                EnsureWatcher();

                PassResult result;
                try
                {
                    result = pass.Run(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Error($"Unexpected error during scan (will retry): {ex}");
                    result = new PassResult(0, 0, 1, ProblemRetry, PassProblem.None);
                }

                if (result.Moved > 0)
                    _lastMove = _clock.GetUtcNow();
                if (result.Problem != PassProblem.None)
                    DisposeWatcher();

                Report(result);

                wait = result.Problem != PassProblem.None
                    ? ProblemRetry
                    : TimeSpan.FromSeconds(_settings.SweepIntervalSeconds);
                if (_watcher is null && wait > ProblemRetry)
                    wait = ProblemRetry; // no live watcher -> poll more often
                if (result.RecheckIn is { } recheck && recheck < wait)
                    wait = recheck;
                if (wait < MinWait)
                    wait = MinWait;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            DisposeWatcher();
            SetStatus(EngineStatus.Now(EngineState.Stopped, "Stopped", _lastMove));
        }
    }

    private void Report(PassResult r)
    {
        var lastMove = _lastMove is { } t ? $" · last move {t.ToLocalTime():HH:mm:ss}" : "";
        var status = r switch
        {
            { Problem: PassProblem.SourceMissing } => EngineStatus.Now(EngineState.Problem, "Source folder not found - retrying", _lastMove),
            { Problem: PassProblem.TargetMissing } => EngineStatus.Now(EngineState.Problem, "Target folder not found - retrying", _lastMove),
            { Failed: > 0 } => EngineStatus.Now(EngineState.Problem, $"{r.Failed} item(s) could not be moved - retrying (see log){lastMove}", _lastMove),
            { Pending: > 0 } => EngineStatus.Now(EngineState.Waiting, $"Waiting for {r.Pending} file(s) to finish writing{lastMove}", _lastMove),
            _ => EngineStatus.Now(EngineState.Idle, $"Watching for new files{lastMove}", _lastMove),
        };
        SetStatus(status);
    }

    private void SetStatus(EngineStatus status)
    {
        if (status.State == Status.State && status.Message == Status.Message)
            return;
        Status = status;
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
            // Listener problems are not our problem.
        }
    }

    private void Kick()
    {
        try
        {
            _kick.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void EnsureWatcher()
    {
        if (_watcherBroken)
            DisposeWatcher();
        if (_watcher is not null || !Directory.Exists(_settings.SourceFolder))
            return;

        try
        {
            var watcher = new FileSystemWatcher(_settings.SourceFolder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += (_, _) => Kick();
            watcher.Changed += (_, _) => Kick();
            watcher.Renamed += (_, _) => Kick();
            watcher.Error += (_, e) =>
            {
                _log.Warning($"File watcher error ({e.GetException().Message}); restarting it and doing a full sweep.");
                _watcherBroken = true;
                Kick();
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watcherBroken = false;
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not watch the source folder ({ex.Message}); checking every {ProblemRetry.TotalSeconds:0}s instead.");
        }
    }

    private void DisposeWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        _watcherBroken = false;
        if (watcher is null)
            return;
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch
        {
        }
    }
}
