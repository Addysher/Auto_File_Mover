using System.ServiceProcess;
using AutoFileMover.Core;

namespace AutoFileMover.Tray;

/// <summary>Settings, status and live log. Closing the window just hides it to the tray.</summary>
internal sealed class MainForm : Form
{
    private readonly TrayContext _ctx;

    private readonly TextBox _source = new() { Dock = DockStyle.Fill };
    private readonly TextBox _target = new() { Dock = DockStyle.Fill };
    private readonly TextBox _logFolder = new() { Dock = DockStyle.Fill, PlaceholderText = $"default: {AppPaths.LogDirectory}" };
    private readonly (TextBox Name, TextBox Path)[] _destinations =
        Enumerable.Range(1, MoverSettings.MaxDestinationsInUi)
            .Select(i => (new TextBox { Width = 140, PlaceholderText = i == 1 ? "e.g. Saw 1" : "e.g. Saw 2" },
                          new TextBox { Dock = DockStyle.Fill, PlaceholderText = i == 1 ? "" : "optional" }))
            .ToArray();
    private readonly JobsPanel _jobs;
    private readonly NumericUpDown _strip = new() { Minimum = 0, Maximum = 100, Width = 70 };
    private readonly NumericUpDown _settle = new() { Minimum = 1, Maximum = 3600, Width = 70 };
    private readonly NumericUpDown _sweep = new() { Minimum = 10, Maximum = 86400, Width = 70, Increment = 30 };
    private readonly CheckBox _stage = new() { AutoSize = true, Text = "Build new job folders in a hidden staging folder, then publish them complete" };
    private readonly ComboBox _jobSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _pattern = new() { Width = 260, Font = new Font(FontFamily.GenericMonospace, 9f) };
    private readonly Button _patternReset = new() { Text = "Default", AutoSize = true };
    private readonly CheckBox _fallback = new() { AutoSize = true, Text = "If no job name is found inside the file, use the file-name rule (otherwise leave it and log it)" };
    private readonly Button _pickSample = new() { Text = "Test a file…", AutoSize = true };
    private readonly TextBox _sample = new() { Width = 220, PlaceholderText = "file name, or pick a real file →" };
    private readonly Label _samplePreview = new() { AutoSize = true, Anchor = AnchorStyles.Left };

    private readonly Label _mode = new() { AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) };
    private readonly Button _pause = new() { AutoSize = true };
    private readonly Button _runNow = new() { AutoSize = true, Text = "Run now" };
    private readonly Button _serviceStart = new() { AutoSize = true, Text = "Start service" };
    private readonly Button _serviceStop = new() { AutoSize = true, Text = "Stop service" };
    private readonly TextBox _logBox = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };
    private string _lastLogText = "";

    public MainForm(TrayContext ctx)
    {
        _ctx = ctx;

        Text = "Auto File Mover";
        Icon = IconArt.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 600);
        Size = new Size(1000, 760);
        Font = SystemFonts.MessageBoxFont;

        _jobs = new JobsPanel(ctx);
        Controls.Add(BuildLayout());

        _pause.Click += (_, _) => { _ctx.TogglePause(); RefreshStatus(); };
        _runNow.Click += (_, _) => _ctx.RunNow();
        _serviceStart.Click += (_, _) => ServiceControl.RunElevated("start");
        _serviceStop.Click += (_, _) => ServiceControl.RunElevated("stop");
        _jobSource.Items.AddRange(["Inside the file (header)", "File name"]);
        _jobSource.SelectedIndexChanged += (_, _) => { UpdateJobNameControls(); UpdatePreview(); };
        _pattern.TextChanged += (_, _) => UpdatePreview();
        _patternReset.Click += (_, _) => _pattern.Text = JobNamer.DefaultContentPattern;
        _fallback.CheckedChanged += (_, _) => { UpdateJobNameControls(); UpdatePreview(); };
        _pickSample.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "Pick a saw file to test", CheckFileExists = true };
            if (Directory.Exists(_source.Text))
                dialog.InitialDirectory = _source.Text;
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _sample.Text = dialog.FileName;
        };
        _sample.TextChanged += (_, _) => UpdatePreview();
        _strip.ValueChanged += (_, _) => UpdatePreview();

        _timer.Tick += (_, _) => RefreshStatus();
        LoadSettings();
        RefreshStatus();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            RefreshStatus();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_ctx.Exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _timer.Stop();
        base.OnFormClosing(e);
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var jobsTab = new TabPage("Jobs");
        var settingsTab = new TabPage("Settings") { AutoScroll = true, Padding = new Padding(6) };
        var logTab = new TabPage("Log") { Padding = new Padding(6) };
        tabs.TabPages.AddRange([jobsTab, settingsTab, logTab]);
        jobsTab.Controls.Add(_jobs);
        root.Controls.Add(tabs);

        // --- Settings -------------------------------------------------------
        var settingsBox = new GroupBox { Text = "Settings", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddFolderRow(grid, "Source folder:", _source);
        AddFolderRow(grid, "Target folder:", _target);
        for (var i = 0; i < _destinations.Length; i++)
            AddDestinationRow(grid, $"Destination {i + 1}:", _destinations[i].Name, _destinations[i].Path);
        AddRow(grid, "", Hint("Jobs tab: where job folders go next. Name is optional (defaults to the folder name)."));
        AddFolderRow(grid, "Log folder:", _logFolder);
        AddRow(grid, "", Hint("Daily Saw_Mover-yyyy-MM-dd.log files. Leave blank for the default."));

        AddRow(grid, "Job name from:", Flow(_jobSource, Hint("MiTek saw files carry the job on their \"M1\" header line")));
        AddRow(grid, "Header pattern:", Flow(_pattern, _patternReset));
        AddRow(grid, "", _fallback);
        AddRow(grid, "Characters to strip:", Flow(_strip, Hint("file-name rule: removed from the end of the name (before the extension)")));
        AddRow(grid, "Test:", Flow(_sample, _pickSample, _samplePreview));
        AddRow(grid, "Settle time (s):", Flow(_settle, Hint("a file must be unchanged and closed this long before it is moved")));
        AddRow(grid, "Full sweep every (s):", Flow(_sweep, Hint("backup re-scan; new files are normally picked up within seconds")));
        AddRow(grid, "", _stage);

        var save = new Button { Text = "Save", AutoSize = true };
        var revert = new Button { Text = "Revert", AutoSize = true };
        save.Click += (_, _) => SaveSettings();
        revert.Click += (_, _) => LoadSettings();
        AddRow(grid, "", Flow(save, revert));

        settingsBox.Controls.Add(grid);
        settingsTab.Controls.Add(settingsBox);

        // --- Log ------------------------------------------------------------
        var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logPanel.Controls.Add(_logBox);
        var openLogs = new LinkLabel { Text = "Open log folder", AutoSize = true };
        openLogs.LinkClicked += (_, _) => Shell.Open(_ctx.LogFolderToShow);
        logPanel.Controls.Add(openLogs);
        logTab.Controls.Add(logPanel);

        // --- Status (always visible) ----------------------------------------
        var statusBox = new GroupBox { Text = "Automatic mover", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var statusPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var labels = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        labels.Controls.AddRange([_status, _mode]);
        statusPanel.Controls.Add(labels);
        statusPanel.Controls.Add(Flow(_pause, _runNow, _serviceStart, _serviceStop));
        statusBox.Controls.Add(statusPanel);
        root.Controls.Add(statusBox);

        return root;
    }

    private void AddFolderRow(TableLayoutPanel grid, string label, TextBox box)
    {
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = box.Text, UseDescriptionForTitle = true, Description = label.TrimEnd(':') };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                box.Text = dialog.SelectedPath;
        };
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(box);
        grid.Controls.Add(browse);
    }

    private void AddDestinationRow(TableLayoutPanel grid, string label, TextBox name, TextBox path)
    {
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = path.Text, UseDescriptionForTitle = true, Description = label.TrimEnd(':') };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                path.Text = dialog.SelectedPath;
        };

        var pair = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pair.Controls.Add(name);
        pair.Controls.Add(path);

        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(pair);
        grid.Controls.Add(browse);
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(control);
        grid.SetColumnSpan(control, 2);
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left };

    private void LoadSettings()
    {
        MoverSettings s;
        try
        {
            s = _ctx.Store.Load();
        }
        catch (Exception ex)
        {
            TrayContext.ShowError($"Could not read {_ctx.Store.FilePath}:\n{ex.Message}");
            s = new MoverSettings();
        }

        _source.Text = s.SourceFolder;
        _target.Text = s.TargetFolder;
        for (var i = 0; i < _destinations.Length; i++)
        {
            var d = i < s.Destinations.Count ? s.Destinations[i] : null;
            _destinations[i].Name.Text = d?.Name ?? "";
            _destinations[i].Path.Text = d?.Path ?? "";
        }

        _jobs.ApplySettings(s);
        _jobSource.SelectedIndex = s.JobNameSource == JobNameSource.FileName ? 1 : 0;
        _pattern.Text = s.ContentPattern;
        _fallback.Checked = s.FallbackToFileName;
        _strip.Value = Math.Clamp(s.CharactersToStrip, (int)_strip.Minimum, (int)_strip.Maximum);
        _settle.Value = Math.Clamp(s.SettleSeconds, (int)_settle.Minimum, (int)_settle.Maximum);
        _sweep.Value = Math.Clamp(s.SweepIntervalSeconds, (int)_sweep.Minimum, (int)_sweep.Maximum);
        _stage.Checked = s.StageNewFolders;
        _logFolder.Text = s.LogFolder;
        UpdateJobNameControls();
        UpdatePreview();
    }

    private void SaveSettings()
    {
        try
        {
            MoverSettings s;
            try
            {
                s = _ctx.Store.Load(); // keep Paused / retention etc.
            }
            catch
            {
                s = new MoverSettings();
            }

            s.SourceFolder = _source.Text.Trim();
            s.TargetFolder = _target.Text.Trim();
            // Rows beyond what the UI shows (hand-edited settings.json) are kept as they are.
            var extra = s.Destinations.Skip(_destinations.Length).ToList();
            s.Destinations = _destinations
                .Where(d => d.Path.Text.Trim().Length > 0)
                .Select(d => new SendToDestination { Name = d.Name.Text.Trim(), Path = d.Path.Text.Trim() })
                .Concat(extra)
                .ToList();
            ApplyJobNameFields(s);
            s.SettleSeconds = (int)_settle.Value;
            s.SweepIntervalSeconds = (int)_sweep.Value;
            s.StageNewFolders = _stage.Checked;
            s.LogFolder = _logFolder.Text.Trim();

            var errors = s.Validate();
            if (errors.Count > 0)
            {
                TrayContext.ShowError("Please fix:\n• " + string.Join("\n• ", errors));
                return;
            }

            var warnings = new List<string>();
            if (!Directory.Exists(s.SourceFolder))
                warnings.Add($"Source folder does not exist (yet): {s.SourceFolder}");
            if (!Directory.Exists(s.TargetFolder))
                warnings.Add($"Target folder does not exist: {s.TargetFolder}");
            foreach (var d in s.Destinations.Where(d => !Directory.Exists(d.Path)))
                warnings.Add($"Destination \"{d.DisplayName}\" does not exist: {d.Path}");
            if (s.LogFolder.Length > 0 && !CanWriteTo(s.LogFolder, out var why))
                warnings.Add($"Cannot write to the log folder ({why}). Logs will go to {AppPaths.LogDirectory} until it is available.");
            if (warnings.Count > 0 &&
                MessageBox.Show(string.Join("\n", warnings) + "\n\nSave anyway?", "Auto File Mover",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _ctx.Store.Save(s);
            _jobs.ApplySettings(s);
            RefreshStatus();
        }
        catch (Exception ex)
        {
            TrayContext.ShowError($"Could not save settings:\n{ex.Message}\n\n" +
                                  "If the Windows service is installed, run install-service.ps1 as administrator so normal users may edit settings.");
        }
    }

    /// <summary>Creates the folder if needed and writes/deletes a probe file.</summary>
    private static bool CanWriteTo(string folder, out string reason)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".afm-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private void ApplyJobNameFields(MoverSettings s)
    {
        s.JobNameSource = _jobSource.SelectedIndex == 1 ? JobNameSource.FileName : JobNameSource.FileContent;
        s.ContentPattern = string.IsNullOrWhiteSpace(_pattern.Text) ? JobNamer.DefaultContentPattern : _pattern.Text.Trim();
        s.FallbackToFileName = _fallback.Checked;
        s.CharactersToStrip = (int)_strip.Value;
    }

    private void UpdateJobNameControls()
    {
        var content = _jobSource.SelectedIndex != 1;
        _pattern.Enabled = _patternReset.Enabled = _fallback.Enabled = content;
        _strip.Enabled = !content || _fallback.Checked;
    }

    private void UpdatePreview()
    {
        var input = _sample.Text.Trim().Trim('"');
        if (input.Length == 0)
        {
            _samplePreview.Text = "";
            return;
        }

        var s = new MoverSettings();
        ApplyJobNameFields(s);

        string? job;
        string reason;
        if (File.Exists(input))
        {
            try
            {
                job = new JobNameResolver(s).Resolve(input, out reason);
            }
            catch (ArgumentException ex)
            {
                job = null;
                reason = "invalid pattern: " + ex.Message;
            }
        }
        else if (s.JobNameSource == JobNameSource.FileContent && !s.FallbackToFileName)
        {
            job = null;
            reason = "pick a real file to test reading its header";
        }
        else
        {
            job = JobNamer.GetJobName(Path.GetFileName(input), s.CharactersToStrip);
            reason = job is null ? "name too short — would be left in place" : "from file name";
        }

        _samplePreview.ForeColor = job is null ? Color.Firebrick : SystemColors.ControlText;
        _samplePreview.Text = job is null ? reason : $"→ folder  {job}\\   ({reason})";
    }

    private void RefreshStatus()
    {
        var snap = _ctx.GetSnapshot();
        _status.Text = snap.Status.Message;
        _status.ForeColor = snap.Status.State switch
        {
            EngineState.Problem => Color.Firebrick,
            EngineState.NotConfigured or EngineState.Blocked => Color.DarkGoldenrod,
            EngineState.Idle => Color.ForestGreen,
            _ => SystemColors.ControlText,
        };
        _mode.Text = snap.ModeText;
        _pause.Text = snap.Paused ? "Resume" : "Pause";
        _serviceStart.Visible = snap.ServiceInstalled && snap.ServiceState == ServiceControllerStatus.Stopped;
        _serviceStop.Visible = snap.ServiceInstalled && snap.ServiceState == ServiceControllerStatus.Running;

        var lines = LogReader.ReadTail(_ctx.TodaysLogFile, 300);
        var text = string.Join(Environment.NewLine, lines);
        if (text != _lastLogText)
        {
            _lastLogText = text;
            _logBox.Text = text;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
    }
}
