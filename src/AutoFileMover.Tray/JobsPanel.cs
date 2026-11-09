using AutoFileMover.Core;

namespace AutoFileMover.Tray;

/// <summary>
/// Two job lists side by side: the target folder (where the engine drops jobs) on the left, and a
/// send-to destination on the right (picked from a drop-down when more than one is configured).
/// Whole job folders move with the arrow buttons, drag-and-drop, or the right-click "Send to" menu.
/// </summary>
internal sealed class JobsPanel : UserControl
{
    private const string DragFormat = "AutoFileMover.JobPaths";
    private const string TargetName = "Target";

    private readonly TrayContext _ctx;
    private readonly JobList _left;
    private readonly JobList _right;
    private readonly ComboBox _destinationPicker = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Button _toRight = ArrowButton("→");
    private readonly Button _toLeft = ArrowButton("←");
    private readonly Button _refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly TextBox _filter = new() { Width = 220, PlaceholderText = "Filter jobs…" };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 700 };
    private readonly ToolTip _tips = new();

    private List<SendToDestination> _destinations = [];
    private string _targetRoot = "";
    private int _version;
    private bool _busy;
    private bool _applying;

    public JobsPanel(TrayContext ctx)
    {
        _ctx = ctx;
        Dock = DockStyle.Fill;
        Padding = new Padding(6);

        _left = new JobList(this, "left", null);
        _right = new JobList(this, "right", _destinationPicker);

        _destinationPicker.SelectedIndexChanged += (_, _) =>
        {
            if (_applying)
                return;
            ShowDestination(_destinationPicker.SelectedIndex);
            _ = ReloadAsync();
        };
        _toRight.Click += async (_, _) => await MoveAsync(_left.SelectedPaths(), _left.Name, _right.Root, _right.Name);
        _toLeft.Click += async (_, _) => await MoveAsync(_right.SelectedPaths(), _right.Name, _left.Root, _left.Name);
        _refresh.Click += async (_, _) => await ReloadAsync();
        _filter.TextChanged += (_, _) => { _left.Populate(_filter.Text); _right.Populate(_filter.Text); UpdateButtons(); };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await ReloadAsync();
        };

        Controls.Add(BuildLayout());
        UpdateButtons();
    }

    public void ApplySettings(MoverSettings settings)
    {
        _targetRoot = settings.TargetFolder;
        _destinations = settings.Destinations.Select(d => d.Copy()).ToList();
        _left.SetRoot(settings.TargetFolder, TargetName, "Set the target folder on the Settings tab");

        // Keep showing the same destination if it still exists.
        var previous = _right.Name;
        _applying = true;
        _destinationPicker.Items.Clear();
        foreach (var d in _destinations)
            _destinationPicker.Items.Add(d.DisplayName);
        var index = Math.Max(0, _destinations.FindIndex(d => d.DisplayName == previous));
        if (_destinations.Count > 0)
            _destinationPicker.SelectedIndex = index;
        _destinationPicker.Enabled = _destinations.Count > 1;
        _applying = false;

        ShowDestination(_destinations.Count > 0 ? index : -1);
        _ = ReloadAsync();
    }

    protected override async void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
            await ReloadAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _left.Dispose();
            _right.Dispose();
            _debounce.Dispose();
            _tips.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowDestination(int index)
    {
        if (index >= 0 && index < _destinations.Count)
            _right.SetRoot(_destinations[index].Path, _destinations[index].DisplayName, "");
        else
            _right.SetRoot("", "Send-to", "Add a destination on the Settings tab");

        _tips.SetToolTip(_toRight, $"Move selected jobs to {_right.Name}");
        _tips.SetToolTip(_toLeft, $"Move selected jobs back to the {TargetName.ToLowerInvariant()} folder");
        UpdateButtons();
    }

    private Control BuildLayout()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        grid.Controls.Add(_left.Header, 0, 0);
        grid.Controls.Add(_right.Header, 2, 0);
        grid.Controls.Add(_left.View, 0, 1);
        grid.Controls.Add(_right.View, 2, 1);

        var arrows = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Anchor = AnchorStyles.None, Margin = new Padding(8, 0, 8, 0),
        };
        arrows.Controls.Add(_toRight);
        arrows.Controls.Add(_toLeft);
        grid.Controls.Add(arrows, 1, 1);

        var bottom = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        bottom.Controls.AddRange([_filter, _refresh, _status]);
        grid.Controls.Add(bottom, 0, 2);
        grid.SetColumnSpan(bottom, 3);

        return grid;
    }

    private static Button ArrowButton(string text) => new()
    {
        Text = text,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 16f, FontStyle.Bold),
        Size = new Size(64, 48),
        Margin = new Padding(0, 6, 0, 6),
    };

    private void UpdateButtons()
    {
        _toRight.Enabled = !_busy && _left.HasSelection && _right.RootExists;
        _toLeft.Enabled = !_busy && _right.HasSelection && _left.RootExists;
        _refresh.Enabled = !_busy;
    }

    private void ScheduleReload()
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private async Task ReloadAsync()
    {
        var version = ++_version;
        var leftRoot = _left.Root;
        var rightRoot = _right.Root;

        var (leftJobs, rightJobs) = await Task.Run(() => (JobFolders.List(leftRoot), JobFolders.List(rightRoot)));
        if (version != _version || IsDisposed)
            return; // a newer reload started meanwhile

        _left.SetJobs(leftJobs, _filter.Text);
        _right.SetJobs(rightJobs, _filter.Text);
        UpdateButtons();
    }

    /// <summary>Every place a job could be sent from the given side (for the right-click menu).</summary>
    private IEnumerable<(string Name, string Root)> OtherPlaces(JobList from)
    {
        if (from == _right && _targetRoot.Length > 0)
            yield return ($"{TargetName} folder", _targetRoot);
        foreach (var d in _destinations)
        {
            if (!string.Equals(d.Path, from.Root, StringComparison.OrdinalIgnoreCase))
                yield return (d.DisplayName, d.Path);
        }
    }

    private async Task MoveAsync(IReadOnlyList<string> paths, string fromName, string toRoot, string toName)
    {
        if (_busy || paths.Count == 0)
            return;

        if (toRoot.Length == 0 || !Directory.Exists(toRoot))
        {
            TrayContext.ShowError($"{toName} is not available:\n{toRoot}");
            return;
        }

        var clashes = paths.Where(p => JobFolders.ExistsIn(p, toRoot)).Select(Path.GetFileName).ToList();
        if (clashes.Count > 0)
        {
            var names = string.Join(", ", clashes.Take(10)) + (clashes.Count > 10 ? $" and {clashes.Count - 10} more" : "");
            var answer = MessageBox.Show(this,
                $"{clashes.Count} job(s) already exist in {toName}:\n{names}\n\n" +
                "Merge into the existing folders? Files with the same name will be overwritten.",
                "Auto File Mover", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
                return;
        }

        _busy = true;
        UpdateButtons();
        _status.Text = $"Moving {paths.Count} job(s) to {toName}…";

        var results = await Task.Run(() => paths.Select(p => JobFolders.Move(p, toRoot)).ToList());

        var log = _ctx.Log;
        foreach (var r in results)
        {
            if (r.Success)
                log.Info($"Job {r.Job} moved {fromName} -> {toName}{(r.Merged ? " (merged into existing folder)" : "")} ({r.FilesMoved} file(s)).");
            else
                log.Warning($"Job {r.Job} could not be fully moved {fromName} -> {toName}: {string.Join("; ", r.Errors)}");
        }

        var ok = results.Count(r => r.Success);
        _status.Text = $"Moved {ok} of {results.Count} job(s) to {toName} at {DateTime.Now:HH:mm}.";

        _busy = false;
        await ReloadAsync();
        var landedIn = string.Equals(toRoot, _left.Root, StringComparison.OrdinalIgnoreCase) ? _left
            : string.Equals(toRoot, _right.Root, StringComparison.OrdinalIgnoreCase) ? _right
            : null;
        landedIn?.Select(results.Where(r => r.Success).Select(r => r.Job));

        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
        {
            TrayContext.ShowError("Some jobs could not be moved completely (files may be open in another program):\n\n" +
                                  string.Join("\n", failed.Select(f => $"• {f.Job}: {f.Errors.FirstOrDefault()}")) +
                                  "\n\nWhatever could not be moved is still in its original folder.");
        }
    }

    /// <summary>One side: header, list view, cached jobs, context menu and a folder watcher.</summary>
    private sealed class JobList : IDisposable
    {
        private readonly JobsPanel _owner;
        private readonly string _id;
        private readonly Label _title = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Anchor = AnchorStyles.Left };
        private readonly LinkLabel _path = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Top, Height = 22 };
        private readonly ToolTip _pathTip = new();
        private readonly ContextMenuStrip _menu = new();
        private List<JobFolderInfo> _jobs = [];
        private FileSystemWatcher? _watcher;
        private int _sortColumn;
        private bool _sortDescending;

        public JobList(JobsPanel owner, string id, Control? picker)
        {
            _owner = owner;
            _id = id;

            Header = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            Header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Header.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // one line; long paths end in "…"
            if (picker is null)
            {
                Header.Controls.Add(_title);
            }
            else
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
                row.Controls.Add(picker);
                row.Controls.Add(_title);
                Header.Controls.Add(row);
            }

            Header.Controls.Add(_path);
            _path.LinkClicked += (_, _) => Shell.Open(Root);

            View = new ListView
            {
                Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true, MultiSelect = true,
                HideSelection = false, AllowDrop = true, ContextMenuStrip = _menu,
            };
            View.Columns.Add("Job", 200);
            View.Columns.Add("Files", 60, HorizontalAlignment.Right);
            View.Columns.Add("Last changed", 130);

            View.SelectedIndexChanged += (_, _) => _owner.UpdateButtons();
            View.DoubleClick += (_, _) =>
            {
                if (View.SelectedItems.Count == 1 && View.SelectedItems[0].Tag is JobFolderInfo job)
                    Shell.Open(job.FullPath);
            };
            View.ColumnClick += (_, e) =>
            {
                _sortDescending = e.Column == _sortColumn && !_sortDescending;
                _sortColumn = e.Column;
                Populate(_owner._filter.Text);
            };
            View.KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A)
                {
                    foreach (ListViewItem item in View.Items)
                        item.Selected = true;
                }
            };

            _menu.Opening += (_, e) => BuildMenu(e);

            View.ItemDrag += (_, _) =>
            {
                if (_owner._busy || View.SelectedItems.Count == 0)
                    return;
                var payload = _id + "\n" + Name + "\n" + string.Join("\n", SelectedPaths());
                View.DoDragDrop(new DataObject(DragFormat, payload), DragDropEffects.Move);
            };
            View.DragOver += (_, e) => e.Effect = CanAccept(e.Data) ? DragDropEffects.Move : DragDropEffects.None;
            View.DragDrop += async (_, e) =>
            {
                if (!CanAccept(e.Data) || e.Data?.GetData(DragFormat) is not string payload)
                    return;
                var lines = payload.Split('\n');
                await _owner.MoveAsync(lines.Skip(2).ToList(), lines[1], Root, Name);
            };
        }

        /// <summary>Display name of the folder currently shown (used in log lines and messages).</summary>
        public string Name { get; private set; } = "";

        public string Root { get; private set; } = "";

        public TableLayoutPanel Header { get; }

        public ListView View { get; }

        public bool RootExists => Root.Length > 0 && Directory.Exists(Root);

        public bool HasSelection => View.SelectedItems.Count > 0;

        public void SetRoot(string root, string name, string whenEmpty)
        {
            Root = root?.Trim() ?? "";
            Name = name;
            _path.Text = Root.Length == 0 ? whenEmpty : Root;
            _path.LinkArea = Root.Length == 0 ? new LinkArea(0, 0) : new LinkArea(0, Root.Length);
            _pathTip.SetToolTip(_path, Root.Length == 0 ? "" : Root + "\n(click to open)");
            View.Enabled = Root.Length > 0;
            _jobs = [];
            Populate(_owner._filter.Text);
            RecreateWatcher();
        }

        public IReadOnlyList<string> SelectedPaths() =>
            View.SelectedItems.Cast<ListViewItem>().Select(i => ((JobFolderInfo)i.Tag!).FullPath).ToList();

        public void SetJobs(List<JobFolderInfo> jobs, string filter)
        {
            _jobs = jobs;
            if (Root.Length > 0 && !Directory.Exists(Root))
                _path.Text = Root + "   (not found)";
            else if (Root.Length > 0)
                _path.Text = Root;
            Populate(filter);
        }

        public void Populate(string filter)
        {
            var selected = View.SelectedItems.Cast<ListViewItem>().Select(i => i.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var query = _jobs.Where(j => filter.Length == 0 || j.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
            IEnumerable<JobFolderInfo> sorted = _sortColumn switch
            {
                1 => query.OrderBy(j => j.FileCount),
                2 => query.OrderBy(j => j.LastWriteTime),
                _ => query.OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase),
            };
            if (_sortDescending)
                sorted = sorted.Reverse();

            View.BeginUpdate();
            View.Items.Clear();
            foreach (var job in sorted)
            {
                var item = new ListViewItem(job.Name) { Tag = job };
                item.SubItems.Add(job.FileCount < 0 ? "?" : job.FileCount.ToString());
                item.SubItems.Add(job.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                item.Selected = selected.Contains(job.Name);
                View.Items.Add(item);
            }

            View.EndUpdate();

            var shown = View.Items.Count;
            var noun = _jobs.Count == 1 ? "job" : "jobs";
            var count = shown == _jobs.Count ? $"({_jobs.Count} {noun})" : $"({shown} of {_jobs.Count} {noun})";
            // The left side shows its name in the title; the right side's name is in the drop-down.
            _title.Text = _id == "left" ? $"{Name}  {count}" : count;
        }

        public void Select(IEnumerable<string> jobNames)
        {
            var names = jobNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0)
                return;
            View.SelectedItems.Clear();
            foreach (ListViewItem item in View.Items)
            {
                if (names.Contains(item.Text))
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
            }

            View.Focus();
        }

        public void Dispose()
        {
            DisposeWatcher();
            _pathTip.Dispose();
            _menu.Dispose();
        }

        private void BuildMenu(System.ComponentModel.CancelEventArgs e)
        {
            _menu.Items.Clear();
            var paths = SelectedPaths();
            if (paths.Count == 0 || _owner._busy)
            {
                e.Cancel = true;
                return;
            }

            if (paths.Count == 1)
                _menu.Items.Add("Open in Explorer", null, (_, _) => Shell.Open(paths[0]));

            var places = _owner.OtherPlaces(this).ToList();
            if (places.Count > 0)
            {
                var sendTo = new ToolStripMenuItem(paths.Count == 1 ? "Send to" : $"Send {paths.Count} jobs to");
                foreach (var (name, root) in places)
                {
                    var item = new ToolStripMenuItem(name, null, async (_, _) => await _owner.MoveAsync(paths, Name, root, name))
                    {
                        Enabled = Directory.Exists(root),
                        ToolTipText = root,
                    };
                    sendTo.DropDownItems.Add(item);
                }

                _menu.Items.Add(sendTo);
            }

            e.Cancel = _menu.Items.Count == 0;
        }

        private bool CanAccept(IDataObject? data)
        {
            if (_owner._busy || !RootExists || data?.GetDataPresent(DragFormat) != true)
                return false;
            return data.GetData(DragFormat) is string payload && !payload.StartsWith(_id + "\n", StringComparison.Ordinal);
        }

        private void RecreateWatcher()
        {
            DisposeWatcher();
            if (!RootExists)
                return;
            try
            {
                var watcher = new FileSystemWatcher(Root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
                };
                watcher.Created += (_, _) => _owner.ScheduleReload();
                watcher.Deleted += (_, _) => _owner.ScheduleReload();
                watcher.Renamed += (_, _) => _owner.ScheduleReload();
                watcher.Error += (_, _) => _owner.ScheduleReload();
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch
            {
                // Network folders may not support watching; Refresh still works.
            }
        }

        private void DisposeWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
