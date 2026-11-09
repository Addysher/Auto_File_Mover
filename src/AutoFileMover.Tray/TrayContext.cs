using System.ServiceProcess;
using AutoFileMover.Core;

namespace AutoFileMover.Tray;

internal sealed record Snapshot(bool ServiceInstalled, ServiceControllerStatus? ServiceState, EngineStatus Status, bool Paused)
{
    public string ModeText => ServiceInstalled
        ? $"Controlling the Windows service ({ServiceState})"
        : "Standalone — this app is moving the files";
}

/// <summary>
/// The tray icon. If the Windows service is installed, this app only edits settings and shows
/// status/logs. Otherwise it runs the engine itself, in the background (never on the UI thread).
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly MoverLog _log = new(AppPaths.LogDirectory);
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;

    private EngineHost? _host;
    private CancellationTokenSource? _hostCts;
    private Task? _hostTask;
    private MainForm? _form;
    private EngineState? _shownState;

    public TrayContext(bool startMinimized)
    {
        Store = new SettingsStore(AppPaths.SettingsFile);
        _log.LogDirectory = SafeLoad().EffectiveLogFolder;
        ImportLegacySettings();

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open Auto File Mover", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) },
            new ToolStripMenuItem("Run now", null, (_, _) => RunNow()),
            _pauseItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open target folder", null, (_, _) => Shell.Open(SafeLoad().TargetFolder)),
            new ToolStripMenuItem("Open log folder", null, (_, _) => Shell.Open(LogFolderToShow)),
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        });
        menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled;

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.For(EngineState.Starting),
            Text = "Auto File Mover",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();

        if (!startMinimized)
            ShowWindow();
    }

    public SettingsStore Store { get; }

    public MoverLog Log => _log;

    /// <summary>Today's log file to display: the configured folder, or the fallback if that is where lines went.</summary>
    public string TodaysLogFile
    {
        get
        {
            var configured = MoverLog.FileFor(_log.LogDirectory, DateTime.Now);
            var fallback = MoverLog.FileFor(_log.FallbackDirectory, DateTime.Now);
            if (_log.ActiveDirectory != _log.LogDirectory)
                return fallback; // this process could not write to the configured folder
            return File.Exists(configured) || !File.Exists(fallback) ? configured : fallback;
        }
    }

    public string LogFolderToShow => Path.GetDirectoryName(TodaysLogFile)!;

    public bool Exiting { get; private set; }

    public Snapshot GetSnapshot()
    {
        var serviceState = ServiceControl.GetStatus();
        var paused = SafeLoad().Paused;

        EngineStatus status;
        if (serviceState is null)
        {
            status = _host?.Status ?? EngineStatus.Now(EngineState.Starting, "Starting");
        }
        else if (serviceState == ServiceControllerStatus.Running)
        {
            status = StatusFile.TryRead(AppPaths.StatusFile) ?? EngineStatus.Now(EngineState.Starting, "Service is running");
        }
        else
        {
            status = EngineStatus.Now(EngineState.Stopped, $"Windows service is {serviceState}");
        }

        return new Snapshot(serviceState is not null, serviceState, status, paused);
    }

    public void RunNow()
    {
        if (_host is not null)
        {
            _host.RequestSweep();
            return;
        }

        try
        {
            File.WriteAllText(AppPaths.SweepRequestFile, DateTime.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            ShowError($"Could not ask the service to run: {ex.Message}");
        }
    }

    public void TogglePause()
    {
        try
        {
            var settings = Store.Load();
            settings.Paused = !settings.Paused;
            Store.Save(settings);
            Refresh();
        }
        catch (Exception ex)
        {
            ShowError($"Could not change the settings file:\n{ex.Message}\n\n" +
                      "If the Windows service is installed, run install-service.ps1 as administrator so normal users may edit settings.");
        }
    }

    public void ShowWindow()
    {
        if (_form is null || _form.IsDisposed)
            _form = new MainForm(this);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    public static void ShowError(string message) =>
        MessageBox.Show(message, "Auto File Mover", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void Refresh()
    {
        var installed = ServiceControl.GetStatus() is not null;
        if (installed && _host is not null)
        {
            StopStandaloneEngine();
            _log.Info("Windows service detected — tray app now only controls the service.");
        }
        else if (!installed && _host is null && !Exiting)
        {
            StartStandaloneEngine();
        }

        // In standalone mode EngineHost applies the log folder (and logs the change); otherwise follow settings here.
        if (_host is null)
            _log.LogDirectory = SafeLoad().EffectiveLogFolder;

        var snap = GetSnapshot();
        _statusItem.Text = Truncate(snap.Status.Message, 60);
        _pauseItem.Text = snap.Paused ? "Resume" : "Pause";
        _icon.Text = Truncate("Auto File Mover — " + snap.Status.Message, 127);
        if (_shownState != snap.Status.State)
        {
            _icon.Icon = TrayIcons.For(snap.Status.State);
            _shownState = snap.Status.State;
        }
    }

    private void StartStandaloneEngine()
    {
        _host = new EngineHost(_log, Store);
        _hostCts = new CancellationTokenSource();
        var host = _host;
        var token = _hostCts.Token;
        _hostTask = Task.Run(() => host.RunAsync(token));
    }

    private void StopStandaloneEngine()
    {
        _hostCts?.Cancel();
        try
        {
            _hostTask?.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
        }

        _hostCts?.Dispose();
        _host = null;
        _hostCts = null;
        _hostTask = null;
    }

    private void ToggleStartup()
    {
        try
        {
            StartupRegistration.Set(!StartupRegistration.IsEnabled);
        }
        catch (Exception ex)
        {
            ShowError($"Could not change the startup setting: {ex.Message}");
        }
    }

    private void Exit()
    {
        Exiting = true;
        _timer.Stop();
        StopStandaloneEngine();
        _icon.Visible = false;
        _icon.Dispose();
        _form?.Close();
        ExitThread();
    }

    private MoverSettings SafeLoad()
    {
        try
        {
            return Store.Load();
        }
        catch
        {
            return new MoverSettings();
        }
    }

    /// <summary>First run: pick up the folders from the old Python version's settings.ini if it sits next to the exe.</summary>
    private void ImportLegacySettings()
    {
        if (Store.Exists)
            return;

        var settings = new MoverSettings();
        foreach (var dir in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var ini = Path.Combine(dir, "settings.ini");
            if (SettingsStore.TryImportLegacyIni(ini, settings))
            {
                _log.Info($"Imported folders from old settings file {ini}.");
                break;
            }
        }

        try
        {
            Store.Save(settings);
        }
        catch
        {
            // No permission yet; the settings window will report it on save.
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
