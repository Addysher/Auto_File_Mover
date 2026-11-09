namespace AutoFileMover.Core;

/// <summary>
/// Everything a host (service or tray app) needs: takes the single-engine lock, loads
/// settings.json, runs the engine, and restarts it whenever the settings file changes
/// (including Pause/Resume). Also honours "sweep.request" files dropped by the tray app.
/// </summary>
public sealed class EngineHost
{
    private static readonly TimeSpan BlockedRetry = TimeSpan.FromSeconds(30);

    private readonly MoverLog _log;
    private readonly SettingsStore _store;
    private readonly SemaphoreSlim _reload = new(0, 1);
    private MoverEngine? _engine;

    public EngineHost(MoverLog log, SettingsStore store)
    {
        _log = log;
        _store = store;
        Status = EngineStatus.Now(EngineState.Starting, "Starting");
    }

    /// <summary>When true, every status change is also written to status.json (used by the service).</summary>
    public bool PublishStatusFile { get; init; }

    public EngineStatus Status { get; private set; }

    public event Action<EngineStatus>? StatusChanged;

    public void RequestSweep() => _engine?.RequestSweep();

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);

        using var guard = await AcquireGuardAsync(ct).ConfigureAwait(false);
        if (guard is null)
            return;

        using var dataWatcher = CreateDataWatcher();
        ConsumeSweepRequest(); // stale request from before we started

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                MoverSettings settings;
                string json;
                try
                {
                    settings = _store.Load();
                    json = SettingsStore.Serialize(settings);
                }
                catch (Exception ex)
                {
                    _log.Error($"Could not read settings file \"{_store.FilePath}\": {ex.Message}");
                    SetStatus(EngineStatus.Now(EngineState.NotConfigured, "Settings file is invalid - see log"));
                    await WaitForSettingsChangeAsync(null, ct).ConfigureAwait(false);
                    continue;
                }

                _log.RetentionDays = settings.LogRetentionDays;
                if (!string.Equals(_log.LogDirectory, settings.EffectiveLogFolder, StringComparison.OrdinalIgnoreCase))
                {
                    var previous = _log.LogDirectory;
                    _log.Info($"Log folder changed to \"{settings.EffectiveLogFolder}\".");
                    _log.LogDirectory = settings.EffectiveLogFolder;
                    _log.Info($"Log folder set (previously \"{previous}\").");
                }

                var errors = settings.Validate();
                if (errors.Count > 0)
                {
                    _log.Warning($"Not moving files - settings incomplete: {string.Join("; ", errors)}.");
                    SetStatus(EngineStatus.Now(EngineState.NotConfigured, "Not configured: " + errors[0]));
                    await WaitForSettingsChangeAsync(json, ct).ConfigureAwait(false);
                    continue;
                }

                if (settings.Paused)
                {
                    _log.Info("Paused.");
                    SetStatus(EngineStatus.Now(EngineState.Paused, "Paused"));
                    await WaitForSettingsChangeAsync(json, ct).ConfigureAwait(false);
                    _log.Info("Settings changed.");
                    continue;
                }

                var engine = new MoverEngine(settings, _log);
                engine.StatusChanged += SetStatus;
                _engine = engine;

                using var engineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var run = engine.RunAsync(engineCts.Token);
                var change = WaitForSettingsChangeAsync(json, engineCts.Token);
                var first = await Task.WhenAny(run, change).ConfigureAwait(false);

                engineCts.Cancel();
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _log.Error($"Engine crashed: {ex}");
                }

                try
                {
                    await change.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                _engine = null;
                engine.StatusChanged -= SetStatus;
                ct.ThrowIfCancellationRequested();

                if (first == run)
                {
                    _log.Error($"Engine stopped unexpectedly; restarting in {BlockedRetry.TotalSeconds:0}s.");
                    await Task.Delay(BlockedRetry, ct).ConfigureAwait(false);
                }
                else
                {
                    _log.Info("Settings changed - applying.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            SetStatus(EngineStatus.Now(EngineState.Stopped, "Stopped"));
        }
    }

    private async Task<SingleEngineGuard?> AcquireGuardAsync(CancellationToken ct)
    {
        var warned = false;
        while (true)
        {
            var guard = SingleEngineGuard.TryAcquire(AppPaths.LockFile);
            if (guard is not null)
            {
                if (warned)
                    _log.Info("The other copy has stopped; this one is now moving files.");
                return guard;
            }

            if (!warned)
            {
                _log.Warning("Another copy of Auto File Mover (service or tray app) is already moving files; waiting.");
                warned = true;
            }

            SetStatus(EngineStatus.Now(EngineState.Blocked, "Another copy is already moving files"));
            try
            {
                await Task.Delay(BlockedRetry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>Waits until settings.json changes to something different from <paramref name="current"/>.</summary>
    private async Task WaitForSettingsChangeAsync(string? current, CancellationToken ct)
    {
        while (true)
        {
            await _reload.WaitAsync(ct).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
            while (_reload.Wait(0))
            {
            }

            string json;
            try
            {
                json = SettingsStore.Serialize(_store.Load());
            }
            catch
            {
                return; // let the main loop report the bad file
            }

            if (json != current)
                return;
        }
    }

    private FileSystemWatcher? CreateDataWatcher()
    {
        try
        {
            var watcher = new FileSystemWatcher(AppPaths.DataDirectory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            void OnName(string? name)
            {
                if (string.Equals(name, Path.GetFileName(_store.FilePath), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _reload.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                    }
                }
                else if (string.Equals(name, AppPaths.SweepRequestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (ConsumeSweepRequest())
                    {
                        _log.Info("Sweep requested from the tray app.");
                        RequestSweep();
                    }
                }
            }

            watcher.Created += (_, e) => OnName(e.Name);
            watcher.Changed += (_, e) => OnName(e.Name);
            watcher.Renamed += (_, e) => OnName(e.Name);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not watch \"{AppPaths.DataDirectory}\" for settings changes ({ex.Message}). Restart to apply changes.");
            return null;
        }
    }

    private static bool ConsumeSweepRequest()
    {
        try
        {
            if (!File.Exists(AppPaths.SweepRequestFile))
                return false;
            File.Delete(AppPaths.SweepRequestFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SetStatus(EngineStatus status)
    {
        Status = status;
        if (PublishStatusFile)
            StatusFile.Write(AppPaths.StatusFile, status);
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch
        {
        }
    }
}
