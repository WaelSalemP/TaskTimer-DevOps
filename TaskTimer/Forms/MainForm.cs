using TaskTimer.Models;
using TaskTimer.Services;
using PowerState = TaskTimer.Services.PowerState;

namespace TaskTimer.Forms;

public sealed class MainForm : Form
{
    private sealed class CallSession
    {
        public DateTime StartUtc = DateTime.UtcNow;
        public DateTime? EndUtc;
        public bool Auto;
        public EntryKind Kind = EntryKind.Call;
        public WorkItem? Target;
        /// <summary>Task whose timer was interrupted and must resume after the call.</summary>
        public List<WorkItem> ResumeTasks = new();
        /// <summary>Whether the "call started" question has been shown yet.</summary>
        public bool Asked;
    }

    /// <summary>Calls shorter than this are not asked about and not recorded.</summary>
    private double MinCallSeconds => Math.Max(0, _settings.MinCallMinutes) * 60.0;

    private sealed record FilterOption(string Value, string Text)
    {
        public override string ToString() => Text;
    }

    private const int DetectEveryTicks = 3;
    private const int AutoSyncEveryTicks = 15 * 60;
    private const int SaveActivityEveryTicks = 60;
    private const int WorkFilePresenceSeconds = 60;
    /// <summary>Idle limit for counting working time when the idle pause itself is switched off.</summary>
    private const int DefaultActivityIdleSeconds = 300;

    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly TimeLog _log = TimeLog.Load();
    private readonly LocalTaskStore _localTasks = LocalTaskStore.Load();
    private readonly WorkdayTracker _workday = WorkdayTracker.Load();
    private readonly SessionMonitor _session = new();
    private WorkFileWatcher? _fileWatcher;
    private DateTime _lastTickUtc = DateTime.UtcNow;
    private bool _startupResumeDone;
    private List<WorkItem> _items = new();
    private List<SprintInfo> _sprints = new();
    private bool _loadingFilters;
    private const string OpenColumn = "ColOpen";
    private Bitmap? _openIcon;
    /// <summary>True while the app itself is fitting columns, so those changes are not taken for the user's.</summary>
    private bool _autoSizingColumns;
    private bool _windowShown;
    /// <summary>Column the list is sorted by; null shows the parent/child hierarchy instead.</summary>
    private string? _sortColumn;
    private bool _sortAscending = true;

    /// <summary>The task started most recently; calls and meetings are offered to it first.</summary>
    private WorkItem? _activeTask;
    /// <summary>Running timers by task key. Holds at most one unless several tasks at once are allowed.</summary>
    private readonly Dictionary<string, (WorkItem Item, DateTime StartUtc)> _running = new();
    private CallSession? _call;
    private bool _dialogOpen;
    private bool _suppressAutoCall;
    /// <summary>Tasks whose timers were paused for inactivity and resume on the next input.</summary>
    private List<WorkItem> _idlePausedTasks = new();
    private int _tick;
    private int _offReadings;
    private string _statusKey = "Ready";
    private object[] _statusArgs = Array.Empty<object>();

    private readonly Panel _toolsHost = new() { Dock = DockStyle.Top, Padding = new Padding(10, 20, 10, 4) };
    private readonly GroupBox _toolsGroup = new() { Dock = DockStyle.Fill, Padding = new Padding(10, 12, 10, 20) };
    private readonly FlowLayoutPanel _toolsFlow = new() { Dock = DockStyle.Fill };
    private readonly Label _accountLabel = NewFilterLabel();
    private readonly ComboBox _accountCombo = NewFilterCombo(200);
    private bool _loadingAccounts;
    private readonly Button _syncButton = NewToolButton();
    private readonly Button _settingsButton = NewToolButton();
    private readonly Button _teamButton = NewToolButton();
    private readonly Label _sprintLabel = NewFilterLabel();
    private readonly ComboBox _sprintCombo = NewFilterCombo(320);
    private readonly Label _stateLabel = NewFilterLabel();
    private readonly ComboBox _stateCombo = NewFilterCombo(180);
    private readonly Label _customerLabel = NewFilterLabel();
    private readonly ComboBox _customerCombo = NewFilterCombo(260);
    private readonly Label _assigneeLabel = NewFilterLabel();
    private readonly ComboBox _assigneeCombo = NewFilterCombo(160);
    private readonly TextBox _searchBox = new() { Width = 260, Margin = new Padding(3, 6, 3, 3) };
    private readonly FlowLayoutPanel _actionsFlow = NewSectionFlow();
    private readonly FlowLayoutPanel _filtersFlow = NewSectionFlow();
    private readonly FlowLayoutPanel _searchFlow = NewSectionFlow();
    private readonly GroupBox _actionsGroup = NewSection();
    private readonly GroupBox _filtersGroup = NewSection();
    private readonly GroupBox _searchGroup = NewSection();
    private readonly FlowLayoutPanel _dateFlow = NewSectionFlow();
    private readonly GroupBox _dateGroup = NewSection();
    private readonly Label _hijriLabel = NewFilterLabel();
    private readonly Label _gregorianLabel = NewFilterLabel();
    private readonly Label _clockLabel = NewFilterLabel();
    private readonly FlowLayoutPanel _machinesFlow = NewSectionFlow();
    private readonly GroupBox _machinesGroup = NewSection();
    private readonly ComboBox _machineCombo = NewFilterCombo(220);
    private readonly Button _connectButton = NewToolButton();
    private readonly Button _machinesButton = NewToolButton();
    private readonly Label _machineStatusLabel = NewFilterLabel();
    private readonly Button _startMachineButton = NewToolButton();
    private readonly Button _stopMachineButton = NewToolButton();
    /// <summary>Guards against an older status answer overwriting the one for a newly selected machine.</summary>
    private int _machineStatusVersion;
    private readonly Button _langButton = NewToolButton();
    private readonly Button _themeButton = NewToolButton();
    private readonly Button _pullRequestsButton = NewToolButton();
    private readonly Button _repositoriesButton = NewToolButton();
    private readonly Label _transparencyLabel = NewFilterLabel();
    // Capped at 70% so the window can never become invisible.
    private readonly TrackBar _transparencyBar = new()
    {
        Minimum = 0, Maximum = 70, SmallChange = 5, LargeChange = 10, TickStyle = TickStyle.None,
        AutoSize = false, Width = 130, Height = 28, Margin = new Padding(3, 6, 3, 3)
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = true, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
    };
    private readonly Label _currentLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8) };
    private readonly Label _taskCaption = new() { AutoSize = true };
    private readonly Label _callCaption = new() { AutoSize = true };
    private readonly Label _taskTime = new() { AutoSize = true, Font = new Font("Segoe UI", 22f, FontStyle.Bold), Text = "00:00:00" };
    private readonly Label _callTime = new() { AutoSize = true, Font = new Font("Segoe UI", 22f, FontStyle.Bold), Text = "00:00:00" };
    private readonly Button _startButton = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _stopButton = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _callButton = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _pushButton = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _editTimeButton = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _addTaskButton = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _editTaskButton = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _dayStatus = new();
    private readonly Font _parentFont = new("Segoe UI", 9.5f, FontStyle.Italic);
    private readonly NotifyIcon _tray = new() { Icon = Program.AppIcon };
    private readonly ToolStripMenuItem _trayOpen = new();
    private readonly ToolStripMenuItem _trayExit = new();
    private bool _exiting;
    private bool _trayHintShown;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public MainForm()
    {
        Loc.IsArabic = _settings.Language == "ar";
        TimeLog.ActiveConnection = _settings.ActiveConnectionId;
        _log.AssignUnownedEntries(_settings.ActiveConnectionId);
        Theme.IsDark = _settings.DarkTheme;
        Icon = Program.AppIcon;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1400, 620);
        MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        ApplyTheme();

        _actionsFlow.Controls.AddRange(new Control[]
        {
            _accountLabel, _accountCombo, _syncButton, _settingsButton, _teamButton, _pullRequestsButton, _repositoriesButton, _langButton, _themeButton, _transparencyLabel, _transparencyBar
        });
        _transparencyBar.Value = Math.Clamp(_settings.WindowTransparency, _transparencyBar.Minimum, _transparencyBar.Maximum);
        Opacity = 1 - _transparencyBar.Value / 100.0;
        _transparencyBar.ValueChanged += (_, _) => Opacity = 1 - _transparencyBar.Value / 100.0;
        // Save once the drag or key press is over, not on every step of the slider.
        _transparencyBar.MouseCaptureChanged += (_, _) => SaveTransparency();
        _transparencyBar.KeyUp += (_, _) => SaveTransparency();
        _filtersFlow.Controls.AddRange(new Control[]
        {
            _sprintLabel, _sprintCombo, _stateLabel, _stateCombo, _customerLabel, _customerCombo, _assigneeLabel, _assigneeCombo
        });
        _searchFlow.Controls.Add(_searchBox);
        _actionsGroup.Controls.Add(_actionsFlow);
        _filtersGroup.Controls.Add(_filtersFlow);
        _searchGroup.Controls.Add(_searchFlow);
        _machinesFlow.Controls.AddRange(new Control[]
        {
            _machineCombo, _machineStatusLabel, _connectButton, _startMachineButton, _stopMachineButton, _machinesButton
        });
        _machinesGroup.Controls.Add(_machinesFlow);
        RebuildMachines();
        _dateFlow.Controls.AddRange(new Control[] { _hijriLabel, _gregorianLabel, _clockLabel });
        _dateFlow.Padding = new Padding(0, 0, 6, 6);
        foreach (var label in new[] { _hijriLabel, _gregorianLabel, _clockLabel })
            label.Font = new Font("Times New Roman", 24f, FontStyle.Bold);
        _hijriLabel.ForeColor = Theme.Green;
        _gregorianLabel.ForeColor = Theme.Primary;
        _clockLabel.ForeColor = Theme.Coral;
        _dateGroup.Controls.Add(_dateFlow);
        _toolsFlow.Controls.AddRange(new Control[] { _actionsGroup, _filtersGroup, _searchGroup, _machinesGroup, _dateGroup });
        _toolsGroup.Controls.Add(_toolsFlow);
        _toolsHost.Controls.Add(_toolsGroup);
        _toolsHost.SizeChanged += (_, _) => SizeToolsGroup();
        _statusStrip.Items.AddRange(new ToolStripItem[] { _status, _dayStatus });

        foreach (var name in new[] { "ColId", "ColType", "ColTitle", "ColState", "ColAssignedTo", "ColProject", "ColSprint", "ColCustomer", "ColCreated", "ColDelivery", "ColCompleted", "ColRemaining", "ColUnsynced" })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, SortMode = DataGridViewColumnSortMode.Programmatic });
        _openIcon = CreateOpenIcon();
        _grid.Columns.Insert(0, new DataGridViewImageColumn
        {
            Name = OpenColumn, Width = _openIcon.Width + 20, Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable, ImageLayout = DataGridViewImageCellLayout.Normal
        });
        foreach (DataGridViewColumn column in _grid.Columns)
            if (_settings.ColumnWidths.TryGetValue(column.Name, out var width))
                column.Width = Math.Max(column.MinimumWidth, width);
        foreach (DataGridViewColumn column in _grid.Columns)
            column.Visible = !_settings.HiddenColumns.Contains(column.Name);

        var timers = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };
        timers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        timers.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        timers.Controls.Add(_taskCaption, 0, 0);
        timers.Controls.Add(_callCaption, 1, 0);
        timers.Controls.Add(_taskTime, 0, 1);
        timers.Controls.Add(_callTime, 1, 1);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _startButton, _stopButton, _callButton, _pushButton, _editTimeButton, _addTaskButton, _editTaskButton });

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(12, 10, 12, 10)
        };
        bottom.Controls.AddRange(new Control[] { _currentLabel, timers, buttons });

        // Docking is resolved from the last-added control backwards, so the fill control goes first.
        Controls.Add(_grid);
        Controls.Add(bottom);
        Controls.Add(_toolsHost);
        Controls.Add(_statusStrip);

        _syncButton.Click += async (_, _) => await SyncAsync();
        _settingsButton.Click += async (_, _) => await OpenSettingsAsync();
        _langButton.Click += (_, _) => ToggleLanguage();
        _themeButton.Click += (_, _) => ToggleTheme();
        _accountCombo.SelectedIndexChanged += async (_, _) => await OnAccountChosenAsync();
        _pullRequestsButton.Click += (_, _) => OpenPullRequests();
        _repositoriesButton.Click += (_, _) => OpenRepositories();
        _teamButton.Click += async (_, _) =>
        {
            using var form = new TeamAccountsForm(_settings);
            if (ShowModal(form) == DialogResult.OK) await SyncAsync(refreshSprints: false);
        };
        foreach (var combo in new[] { _sprintCombo, _stateCombo, _customerCombo, _assigneeCombo })
            combo.SelectedIndexChanged += async (_, _) => await OnFilterChangedAsync();
        _startButton.Click += (_, _) => StartSelectedTask();
        _stopButton.Click += (_, _) => StopByUser();
        _callButton.Click += (_, _) => ToggleCall();
        _pushButton.Click += async (_, _) => await PushAsync();
        _editTimeButton.Click += (_, _) => EditUnsentTime();
        _addTaskButton.Click += async (_, _) => await AddTaskAsync();
        _editTaskButton.Click += async (_, _) =>
        {
            if (_grid.CurrentRow?.Tag is WorkItem { IsListed: true } item) await EditTaskAsync(item);
            else ShowMessage(Loc.T("SelectAnyTask"));
        };
        _connectButton.Click += (_, _) =>
        {
            if (_machineCombo.SelectedItem is DevMachine machine) MachinesForm.Connect(this, machine);
            else ShowMessage(Loc.T("SelectMachine"));
        };
        _machinesButton.Click += (_, _) =>
        {
            using var form = new MachinesForm(_settings);
            ShowModal(form);
            RebuildMachines();
            SizeToolsGroup();
        };
        _machineCombo.SelectedIndexChanged += (_, _) =>
        {
            _ = RefreshMachineStatusAsync(); // also resets the buttons until the new machine's state is known
            if (_machineCombo.SelectedItem is not DevMachine machine || _settings.SelectedMachineId == machine.Id) return;
            _settings.SelectedMachineId = machine.Id;
            SettingsStore.Save(_settings);
        };
        _startMachineButton.Click += async (_, _) => await ChangeMachinePowerAsync(start: true);
        // Clicking the status checks it again, and signs in to Microsoft first when that is needed.
        _machineStatusLabel.Cursor = Cursors.Hand;
        _machineStatusLabel.Click += async (_, _) => await RefreshMachineStatusAsync(interactive: true);
        _stopMachineButton.Click += async (_, _) => await ChangeMachinePowerAsync(start: false);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && !IsOpenCell(e)) StartSelectedTask(); };
        _grid.CellClick += (_, e) => { if (IsOpenCell(e) && _grid.Rows[e.RowIndex].Tag is WorkItem item) OpenInBrowser(item); };
        _grid.CellMouseEnter += (_, e) => { if (IsOpenCell(e)) _grid.Cursor = Cursors.Hand; };
        _grid.CellMouseLeave += (_, _) => _grid.Cursor = Cursors.Default;
        _grid.CellMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex]; // act on the row under the mouse
            if (_grid.Rows[e.RowIndex].Tag is WorkItem item) ShowRowMenu(item);
        };
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.KeyDown += (_, e) =>
        {
            // Only local tasks can be deleted; server tasks are closed or removed through their status.
            if (e.KeyCode != Keys.Delete || _grid.CurrentRow?.Tag is not WorkItem item || !LocalTaskStore.IsLocal(item)) return;
            e.Handled = true;
            DeleteLocalTask(item);
        };
        _grid.SizeChanged += (_, _) => FitColumns();
        _grid.ColumnWidthChanged += (_, e) =>
        {
            // Before the window is shown, width changes come from DPI scaling, not from the user.
            if (_autoSizingColumns || !_windowShown) return;
            _settings.ColumnWidths[e.Column.Name] = e.Column.Width;
            SettingsStore.Save(_settings);
            BeginInvoke(FitColumns); // let the other columns take up or give back the difference
        };
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) SortBy(_grid.Columns[e.ColumnIndex].Name);
            else if (e.Button == MouseButtons.Right) ShowColumnMenu();
        };
        _searchBox.TextChanged += (_, _) => RefreshGrid();
        _timer.Tick += (_, _) => OnTick();

        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Items.AddRange(new ToolStripItem[] { _trayOpen, new ToolStripSeparator(), _trayExit });
        _trayOpen.Font = new Font(_trayOpen.Font, FontStyle.Bold);
        _trayOpen.Click += (_, _) => ShowFromTray();
        _trayExit.Click += (_, _) => { _exiting = true; Close(); };
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.Visible = true;

        ApplyLanguage();
        _timer.Start();
        Shown += async (_, _) =>
        {
            _windowShown = true;
            MergeLocalTasks();
            RebuildFilters();
            RefreshGrid();
            // Needs the window handle: the watcher marshals its events onto this form's thread.
            RestartFileWatcher();
            if (ProviderFactory.IsConfigured(_settings)) await SyncAsync();
            else await OpenSettingsAsync();
        };
    }

    private static FlowLayoutPanel NewSectionFlow() =>
        new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };

    private static GroupBox NewSection() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 6),
        Margin = new Padding(3, 3, 12, 3)
    };

    private void SaveTransparency()
    {
        if (_settings.WindowTransparency == _transparencyBar.Value) return;
        _settings.WindowTransparency = _transparencyBar.Value;
        SettingsStore.Save(_settings);
    }

    private static Button NewToolButton() => new() { AutoSize = true, Padding = new Padding(8, 3, 8, 3) };

    // The top margins line the label text and the combo boxes up with the buttons beside them.
    private static Label NewFilterLabel() => new() { AutoSize = true, Margin = new Padding(10, 10, 0, 0) };

    private static ComboBox NewFilterCombo(int width) =>
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Margin = new Padding(3, 6, 3, 3) };

    /// <summary>Fits the group to its controls, which wrap onto more rows when the window is narrow.</summary>
    private void SizeToolsGroup()
    {
        // A section wider than the window wraps its own controls instead of being cut off.
        var available = Math.Max(240, _toolsGroup.DisplayRectangle.Width - 40);
        foreach (var flow in new[] { _actionsFlow, _filtersFlow, _searchFlow, _machinesFlow, _dateFlow })
            flow.MaximumSize = new Size(available, 0);

        var chrome = _toolsGroup.Height - _toolsGroup.DisplayRectangle.Height + _toolsHost.Padding.Vertical;
        var content = _toolsFlow.GetPreferredSize(new Size(_toolsGroup.DisplayRectangle.Width, 0)).Height;
        if (_toolsHost.Height != content + chrome) _toolsHost.Height = content + chrome;
    }

    // ---------- theme ----------

    private void ToggleTheme()
    {
        Theme.IsDark = !Theme.IsDark;
        _settings.DarkTheme = Theme.IsDark;
        SettingsStore.Save(_settings);
        ApplyTheme();
        _themeButton.Text = Loc.T(Theme.IsDark ? "ThemeLight" : "ThemeDark");
        SizeToolsGroup();
        RefreshGrid(); // rows carry their own colours for parents and running tasks
    }

    /// <summary>Applies the current palette to the whole window; safe to call again after a theme change.</summary>
    private void ApplyTheme()
    {
        Theme.StyleForm(this, Theme.Background);
        Theme.StyleStrip(_statusStrip, Theme.Background);
        Theme.StyleGrid(_grid);
        Theme.StyleButton(_syncButton, Theme.Primary);
        Theme.StyleButton(_settingsButton, Theme.Neutral);
        Theme.StyleButton(_teamButton, Theme.Neutral);
        Theme.StyleButton(_langButton, Theme.Neutral);
        Theme.StyleButton(_themeButton, Theme.Neutral);
        Theme.StyleButton(_pullRequestsButton, Theme.Neutral);
        Theme.StyleButton(_repositoriesButton, Theme.Neutral);
        Theme.StyleButton(_startButton, Theme.Green);
        Theme.StyleButton(_stopButton, Theme.Coral);
        Theme.StyleButton(_callButton, Theme.Violet);
        Theme.StyleButton(_pushButton, Theme.Primary);
        Theme.StyleButton(_editTimeButton, Theme.Neutral);
        Theme.StyleButton(_addTaskButton, Theme.Green);
        Theme.StyleButton(_editTaskButton, Theme.Neutral);
        Theme.StyleButton(_connectButton, Theme.Primary);
        Theme.StyleButton(_machinesButton, Theme.Neutral);
        Theme.StyleButton(_startMachineButton, Theme.Green);
        Theme.StyleButton(_stopMachineButton, Theme.Coral);
        _taskCaption.ForeColor = _callCaption.ForeColor = _status.ForeColor = _dayStatus.ForeColor = Theme.Muted;
    }

    // ---------- language ----------

    private void ToggleLanguage()
    {
        Loc.IsArabic = !Loc.IsArabic;
        _settings.Language = Loc.IsArabic ? "ar" : "en";
        SettingsStore.Save(_settings);
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Loc.ApplyDirection(this);
        Text = Loc.T("AppTitle");
        _toolsGroup.Text = Loc.T("ToolsGroup");
        _trayOpen.Text = Loc.T("TrayOpen");
        _trayExit.Text = Loc.T("TrayExit");
        _tray.ContextMenuStrip!.RightToLeft = Loc.IsArabic ? RightToLeft.Yes : RightToLeft.No;
        _syncButton.Text = Loc.T("Sync");
        _settingsButton.Text = Loc.T("Settings");
        _teamButton.Text = Loc.T("TeamAccounts");
        _langButton.Text = Loc.T("LangButton");
        _sprintLabel.Text = Loc.T("Sprint");
        _stateLabel.Text = Loc.T("State");
        _customerLabel.Text = Loc.T("Customer");
        _assigneeLabel.Text = Loc.T("Assignee");
        _actionsGroup.Text = Loc.T("GroupActions");
        _accountLabel.Text = Loc.T("Account");
        RebuildAccounts();
        _pullRequestsButton.Text = Loc.T("PullRequests");
        _repositoriesButton.Text = Loc.T("Repositories");
        _themeButton.Text = Loc.T(Theme.IsDark ? "ThemeLight" : "ThemeDark");
        _transparencyLabel.Text = Loc.T("Transparency");
        _filtersGroup.Text = Loc.T("GroupFilters");
        _searchGroup.Text = Loc.T("GroupSearch");
        _dateGroup.Text = Loc.T("GroupDate");
        _machinesGroup.Text = Loc.T("GroupMachines");
        _connectButton.Text = Loc.T("Connect");
        _machinesButton.Text = Loc.T("ManageMachines");
        _startMachineButton.Text = Loc.T("StartMachine");
        UpdateMachineButtons();
        UpdateClock();
        _searchBox.PlaceholderText = Loc.T("SearchHint");
        RebuildFilters();
        _taskCaption.Text = Loc.T("TaskTime");
        _callCaption.Text = Loc.T("CallTime");
        _startButton.Text = Loc.T("Start");
        _stopButton.Text = Loc.T("Stop");
        _pushButton.Text = Loc.T("Push");
        _editTimeButton.Text = Loc.T("EditTime");
        _addTaskButton.Text = Loc.T("AddTask");
        _editTaskButton.Text = Loc.T("EditTask");
        foreach (DataGridViewColumn column in _grid.Columns) column.HeaderText = Loc.T(column.Name);
        SizeToolsGroup();
        UpdateUi();
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        _status.Text = Loc.T(key, args);
    }

    private void ShowMessage(string text, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(this, text, Loc.T("AppTitle"), MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1,
            Loc.MsgOptions);

    // ---------- sync / settings ----------

    private async Task OpenSettingsAsync()
    {
        using var form = new SettingsForm(_settings);
        var result = ShowModal(form);
        if (form.DeleteRequested)
        {
            await DeleteActiveAccountAsync();
            return;
        }
        RebuildAccounts(); // the name may have changed
        if (result != DialogResult.OK) return;
        if (!_settings.AllowMultipleTasks)
            foreach (var key in _running.Keys.Where(k => k != _activeTask?.Key).ToList()) StopTask(key);
        RestartFileWatcher();
        await SyncAsync();
    }

    // ---------- work files ----------

    private void RestartFileWatcher()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = new WorkFileWatcher(_settings.WorkPaths, this);
        _fileWatcher.WorkFileActivity += OnWorkFileActivity;
    }

    /// <summary>A file in a work folder was opened or saved: make sure that time is being recorded.</summary>
    private void OnWorkFileActivity(string path)
    {
        if (IsDisposed || TaskRunning || _call != null || _idlePausedTasks.Count > 0) return;

        // Sync tools and builds also touch these folders; only react while the user is at the keyboard.
        if (_session.IsAway || IdleDetector.IdleSeconds > WorkFilePresenceSeconds) return;

        var task = _activeTask ?? TimeableItems.FirstOrDefault(i => i.Key == _settings.LastTaskKey);
        if (task != null) StartTask(task);
        else SetStatus("WorkFileNoTask", Path.GetFileName(path));
        UpdateUi();
    }

    /// <param name="silent">Background refresh: report problems in the status bar instead of a dialog.</param>
    /// <param name="refreshSprints">Also reload the sprint list, which is slow and rarely changes.</param>
    private async Task SyncAsync(bool silent = false, bool refreshSprints = true)
    {
        if (!ProviderFactory.IsConfigured(_settings))
        {
            if (!silent) ShowMessage(Loc.T("NotConfigured"), MessageBoxIcon.Warning);
            return;
        }

        _syncButton.Enabled = _sprintCombo.Enabled = false;
        SetStatus("Syncing");
        try
        {
            using var provider = ProviderFactory.Create(_settings);
            _items = await provider.GetMyItemsAsync(_settings.SelectedSprint);
            MergeLocalTasks();
            if (IsDisposed) return;
            // Keep timing the same task across a refresh, even if it is no longer in the list.
            if (_activeTask != null)
                _activeTask = _items.FirstOrDefault(i => i.Key == _activeTask.Key) ?? _activeTask;
            SetStatus("Synced", MyItems.Count);
            ResumeLastTaskOnce();

            // Show the tasks and their filters right away; the sprint list follows when it arrives.
            RebuildFilters();
            RefreshGrid();
            if (refreshSprints || _sprints.Count == 0) _sprints = await provider.GetSprintsAsync();
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            if (silent) SetStatus("SyncFailed", ex.Message);
            else
            {
                SetStatus("Ready");
                ShowMessage(ex.Message, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _syncButton.Enabled = _sprintCombo.Enabled = true;
                RebuildFilters();
                RefreshGrid();
            }
        }
    }

    /// <summary>After the first successful sync, continues the task that was running when the app closed.</summary>
    private void ResumeLastTaskOnce()
    {
        if (_startupResumeDone) return;
        _startupResumeDone = true;
        if (!_settings.AutoResumeLastTask || !_settings.LastTaskRunning || TaskRunning || _call != null) return;
        if (TimeableItems.FirstOrDefault(i => i.Key == _settings.LastTaskKey) is { } last) StartTask(last);
    }

    /// <summary>The selected row if time can be recorded on it (a Task or Bug in my list), otherwise null.</summary>
    private WorkItem? SelectedItem => _grid.CurrentRow?.Tag is WorkItem { CanLogTime: true } item ? item : null;

    private List<WorkItem> TimeableItems => _items.Where(i => i.CanLogTime).ToList();

    private List<WorkItem> MyItems => _items.Where(i => i.IsListed).ToList();

    /// <summary>Fills the sprint, status and customer lists from the values my loaded items have.</summary>
    private void RebuildFilters()
    {
        _loadingFilters = true;
        try
        {
            var mine = MyItems;
            // Server sprints (ended ones included), plus any the loaded items or the saved choice refer to.
            var current = _sprints.Where(s => s.IsCurrent).Select(s => s.Path).ToHashSet();
            var sprints = _sprints.Select(s => s.Path).Concat(mine.Select(i => i.Sprint)).Append(_settings.SelectedSprint);
            Fill(_sprintCombo, "AllSprints", sprints, _settings.SelectedSprint,
                path => current.Contains(path) ? Loc.T("CurrentSprint", path) : path);
            Fill(_stateCombo, "AllStates", mine.Select(i => i.State), _settings.SelectedState, s => s);
            Fill(_customerCombo, "AllCustomers", mine.Select(i => i.Customer), _settings.SelectedCustomer, s => s);
            Fill(_assigneeCombo, "AllAssignees", mine.Select(i => i.AssignedTo), _settings.SelectedAssignee, s => s);
        }
        finally
        {
            _loadingFilters = false;
        }
    }

    private static void Fill(ComboBox combo, string allKey, IEnumerable<string> values, string selected,
        Func<string, string> display)
    {
        combo.Items.Clear();
        combo.Items.Add(new FilterOption("", Loc.T(allKey)));
        foreach (var value in values.Where(v => v.Length > 0).Distinct()
                     .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase))
            combo.Items.Add(new FilterOption(value, display(value)));

        // A saved value that none of my items has any more falls back to "all".
        combo.SelectedItem = combo.Items.Cast<FilterOption>().FirstOrDefault(o => o.Value == selected) ?? combo.Items[0];
    }

    private static string FilterValue(ComboBox combo) => (combo.SelectedItem as FilterOption)?.Value ?? "";

    private async Task OnFilterChangedAsync()
    {
        if (_loadingFilters) return;
        var sprintChanged = _settings.SelectedSprint != FilterValue(_sprintCombo);
        _settings.SelectedSprint = FilterValue(_sprintCombo);
        _settings.SelectedState = FilterValue(_stateCombo);
        _settings.SelectedCustomer = FilterValue(_customerCombo);
        _settings.SelectedAssignee = FilterValue(_assigneeCombo);
        SettingsStore.Save(_settings);

        // The sprint decides what is fetched (an ended sprint needs its closed items), the other
        // filters only narrow what is already loaded.
        if (sprintChanged && ProviderFactory.IsConfigured(_settings)) await SyncAsync(refreshSprints: false);
        else RefreshGrid();
    }

    /// <summary>
    /// My items matching the sprint, status and customer filters together with all their ancestors, in tree order
    /// (each parent immediately followed by its children) with the nesting depth.
    /// </summary>
    private List<(WorkItem Item, int Depth)> BuildTree()
    {
        var byKey = _items.ToDictionary(i => i.Key);
        var sprint = FilterValue(_sprintCombo);
        var state = FilterValue(_stateCombo);
        var customer = FilterValue(_customerCombo);
        var assignee = FilterValue(_assigneeCombo);
        var search = _searchBox.Text.Trim();
        bool Found(WorkItem i) => search.Length == 0 ||
                                  i.Id.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                  i.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        static bool Matches(string filter, string value) => filter.Length == 0 || filter == value;

        var visible = new HashSet<string>();
        foreach (var item in MyItems.Where(i =>
                     // Local tasks have no sprint and are always the user's own, so those two filters skip them.
                     (LocalTaskStore.IsLocal(i) || Matches(sprint, i.Sprint) && Matches(assignee, i.AssignedTo)) &&
                     Matches(state, i.State) && Matches(customer, i.Customer) && Found(i)))
            for (var node = item; node != null && visible.Add(node.Key);)
                node = byKey.GetValueOrDefault(node.ParentKey);

        var shown = _items.Where(i => visible.Contains(i.Key)).ToList();
        var children = shown.ToLookup(i => visible.Contains(i.ParentKey) ? i.ParentKey : "");

        var result = new List<(WorkItem, int)>();
        void Add(WorkItem item, int depth)
        {
            result.Add((item, depth));
            foreach (var child in children[item.Key]) Add(child, depth + 1);
        }
        foreach (var root in children[""]) Add(root, 0);
        return result;
    }

    /// <summary>Header click: ascending, then descending, then back to the hierarchy.</summary>
    private void SortBy(string column)
    {
        if (column == OpenColumn) return;
        if (_sortColumn != column) (_sortColumn, _sortAscending) = (column, true);
        else if (_sortAscending) _sortAscending = false;
        else _sortColumn = null;

        foreach (DataGridViewColumn c in _grid.Columns)
            c.HeaderCell.SortGlyphDirection = c.Name != _sortColumn ? SortOrder.None
                : _sortAscending ? SortOrder.Ascending : SortOrder.Descending;
        RefreshGrid();
    }

    private IComparable? SortKey(WorkItem item) => _sortColumn switch
    {
        "ColId" => long.TryParse(item.Id, out var number) ? number : null,
        "ColType" => item.Type,
        "ColState" => item.State,
        "ColAssignedTo" => item.AssignedTo,
        "ColProject" => item.Project,
        "ColSprint" => item.Sprint,
        "ColCustomer" => item.Customer,
        "ColCreated" => item.CreatedDate,
        "ColDelivery" => item.DeliveryDate,
        "ColCompleted" => item.CompletedWork,
        "ColRemaining" => item.RemainingWork,
        "ColUnsynced" => _log.PendingSeconds(item.Key),
        _ => item.Title
    };

    /// <summary>
    /// Rows to show: the hierarchy, or - while a sort is active - a flat list of my items in that
    /// order. A tree cannot be ordered by a column of its leaves, so sorting drops the parent rows.
    /// </summary>
    private List<(WorkItem Item, int Depth)> BuildRows()
    {
        var tree = BuildTree();
        if (_sortColumn == null) return tree;

        var rows = tree.Where(r => r.Item.IsListed).Select(r => (r.Item, Depth: 0)).ToList();
        rows.Sort((a, b) =>
        {
            var x = SortKey(a.Item);
            var y = SortKey(b.Item);
            // Empty values go last in both directions.
            var xEmpty = x is null or "";
            var yEmpty = y is null or "";
            if (xEmpty || yEmpty) return xEmpty.CompareTo(yEmpty);
            var result = x is string s ? string.Compare(s, (string)y!, StringComparison.CurrentCultureIgnoreCase) : x!.CompareTo(y);
            return _sortAscending ? result : -result;
        });
        return rows;
    }

    private void RefreshGrid()
    {
        var selectedKey = (_grid.CurrentRow?.Tag as WorkItem)?.Key;
        _grid.Rows.Clear();
        foreach (var (item, depth) in BuildRows())
        {
            // Non-breaking spaces: ordinary leading spaces are trimmed when the cell is painted.
            var title = new string('\u00A0', depth * 5) + item.Title;
            var index = _grid.Rows.Add(_openIcon!, item.Id, item.Type, title, item.State, item.AssignedTo, item.Project, item.Sprint, item.Customer,
                item.CreatedDate?.ToString("yyyy-MM-dd") ?? "", item.DeliveryDate?.ToString("yyyy-MM-dd") ?? "",
                item.CompletedWork?.ToString("0.##") ?? "", item.RemainingWork?.ToString("0.##") ?? "", "");
            var row = _grid.Rows[index];
            row.Tag = item;
            row.Cells[OpenColumn].ToolTipText = Loc.T("OpenInBrowser");
            if (!item.CanLogTime)
                row.DefaultCellStyle.ForeColor = row.DefaultCellStyle.SelectionForeColor = Theme.Muted;
            if (!item.IsListed)
            {
                row.DefaultCellStyle.ForeColor = row.DefaultCellStyle.SelectionForeColor = Theme.Muted;
                row.DefaultCellStyle.Font = _parentFont;
            }
            // The current cell must be in a visible column, and the first one may be hidden.
            if (item.Key == selectedKey && _grid.Columns.GetFirstColumn(DataGridViewElementStates.Visible) is { } first)
                _grid.CurrentCell = row.Cells[first.Index];
        }
        UpdateUi(); // fills the "not yet sent" column, which the fitting below must see
        FitColumns();
    }

    private bool IsOpenCell(DataGridViewCellEventArgs e) =>
        e.RowIndex >= 0 && e.ColumnIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == OpenColumn;

    private void OpenInBrowser(WorkItem item)
    {
        if (item.Url.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowMessage(ex.Message, MessageBoxIcon.Error);
        }
    }

    /// <summary>Draws the "open in new window" glyph: a box with an arrow leaving its top-right corner.</summary>
    private Bitmap CreateOpenIcon()
    {
        var size = 16 * DeviceDpi / 96;
        var unit = size / 16f;
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.Primary, 1.7f * unit)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };
        PointF P(float x, float y) => new(x * unit, y * unit);
        g.DrawLines(pen, new[] { P(7, 3), P(2.5f, 3), P(2.5f, 13.5f), P(13, 13.5f), P(13, 9) }); // box, open at the corner
        g.DrawLine(pen, P(7.5f, 8.5f), P(13.5f, 2.5f));                                           // arrow shaft
        g.DrawLines(pen, new[] { P(9.5f, 2.5f), P(13.5f, 2.5f), P(13.5f, 6.5f) });               // arrow head
        return bitmap;
    }

    /// <summary>Right-click on a task row: copy its ID or title, or open it in the browser.</summary>
    private void ShowRowMenu(WorkItem item)
    {
        var menu = new ContextMenuStrip { RightToLeft = Loc.IsArabic ? RightToLeft.Yes : RightToLeft.No };
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Items.Add(Loc.T("CopyId"), null, (_, _) => CopyText(item.Id));
        menu.Items.Add(Loc.T("CopyTitle"), null, (_, _) => CopyText(item.Title));
        menu.Items.Add(Loc.T("CopyBoth"), null, (_, _) => CopyText(item.ToString()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("OpenInBrowser"), null, (_, _) => OpenInBrowser(item)).Enabled = item.Url.Length > 0;
        if (item.IsListed) menu.Items.Add(Loc.T("EditTask"), null, async (_, _) => await EditTaskAsync(item));
        if (LocalTaskStore.IsLocal(item) && ServerTasksAvailable)
            menu.Items.Add(Loc.T("MoveToServer"), null, async (_, _) => await MoveLocalTaskAsync(item));
        if (LocalTaskStore.IsLocal(item))
            menu.Items.Add(Loc.T("DeleteLocalTask"), null, (_, _) => DeleteLocalTask(item));
        menu.Show(Cursor.Position);
    }

    private void CopyText(string text)
    {
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text);
            SetStatus("Copied", text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another program is holding the clipboard open.
            ShowMessage(ex.Message, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Right-click on a column header: a tick list for showing and hiding columns.</summary>
    private void ShowColumnMenu()
    {
        var menu = new ContextMenuStrip { RightToLeft = Loc.IsArabic ? RightToLeft.Yes : RightToLeft.No };
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);

        var visibleCount = _grid.Columns.Cast<DataGridViewColumn>().Count(c => c.Visible);
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            var text = column.Name == OpenColumn ? Loc.T("OpenInBrowser") : Loc.T(column.Name);
            var item = new ToolStripMenuItem(text)
            {
                Checked = column.Visible,
                Enabled = !column.Visible || visibleCount > 1 // the last visible column cannot be hidden
            };
            item.Click += (_, _) => SetColumnVisible(column, !column.Visible);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        var showAll = new ToolStripMenuItem(Loc.T("ShowAllColumns")) { Enabled = _settings.HiddenColumns.Count > 0 };
        showAll.Click += (_, _) =>
        {
            foreach (DataGridViewColumn column in _grid.Columns) SetColumnVisible(column, true, save: false);
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(showAll);
        menu.Show(Cursor.Position);
    }

    private void SetColumnVisible(DataGridViewColumn column, bool visible, bool save = true)
    {
        column.Visible = visible;
        FitColumns();
        _settings.HiddenColumns.Remove(column.Name);
        if (!visible) _settings.HiddenColumns.Add(column.Name);
        if (save) SettingsStore.Save(_settings);
    }

    private const int MinColumnWidth = 44;
    private const int MinTitleWidth = 160;

    /// <summary>
    /// Sizes the columns so that all of them fit the grid's width without a horizontal scrollbar.
    /// Each column starts from the width its content needs (or the width the user dragged it to).
    /// Spare room goes to the title; when there is too little room the title gives way first,
    /// then the remaining automatic columns shrink in proportion.
    /// </summary>
    private void FitColumns()
    {
        if (_autoSizingColumns) return;
        var columns = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();
        var scrollBar = _grid.Controls.OfType<VScrollBar>().FirstOrDefault(s => s.Visible);
        var available = _grid.ClientSize.Width - (scrollBar?.Width ?? 0) - 2;
        if (columns.Count == 0 || available < 100) return; // window hidden or minimized

        _autoSizingColumns = true;
        try
        {
            var widths = new Dictionary<DataGridViewColumn, int>();
            var flexible = new List<DataGridViewColumn>();
            var timerWidth = TextRenderer.MeasureText("00:00:00", _grid.Font).Width + 24;
            foreach (var column in columns)
            {
                if (column.Name == OpenColumn)
                    widths[column] = column.Width;
                else if (_settings.ColumnWidths.TryGetValue(column.Name, out var chosen))
                    widths[column] = Math.Max(column.MinimumWidth, chosen);
                else
                {
                    var needed = column.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true);
                    // Running timers appear in this column later; leave room for them now.
                    if (column.Name == "ColUnsynced") needed = Math.Max(needed, timerWidth);
                    widths[column] = needed;
                    flexible.Add(column);
                }
            }

            var title = flexible.FirstOrDefault(c => c.Name == "ColTitle");
            var spare = available - widths.Values.Sum();
            if (title != null)
            {
                widths[title] = Math.Max(MinTitleWidth, widths[title] + spare);
                spare = available - widths.Values.Sum();
            }
            else if (spare > 0 && flexible.Count > 0)
            {
                widths[flexible[^1]] += spare;
                spare = 0;
            }

            if (spare < 0 && flexible.Count > 0)
            {
                var room = available - columns.Except(flexible).Sum(c => widths[c]);
                var scale = Math.Max(0.0, room) / flexible.Sum(c => widths[c]);
                foreach (var column in flexible)
                    widths[column] = Math.Max(MinColumnWidth, (int)(widths[column] * scale));
            }

            foreach (var (column, width) in widths)
                if (column.Width != width) column.Width = Math.Max(column.MinimumWidth, width);
        }
        finally
        {
            _autoSizingColumns = false;
        }
    }

    // ---------- task timer ----------

    private bool TaskRunning => _running.Count > 0;

    private bool IsRunning(string key) => _running.ContainsKey(key);

    /// <summary>Seconds of the segment currently being timed on one task (0 when it is not running).</summary>
    private double RunningSeconds(string key) =>
        _running.TryGetValue(key, out var run) ? (DateTime.UtcNow - run.StartUtc).TotalSeconds : 0;

    private double TotalRunningSeconds => _running.Keys.Sum(RunningSeconds);

    private void StartSelectedTask()
    {
        var item = SelectedItem;
        if (item == null)
        {
            ShowMessage(Loc.T(_grid.CurrentRow?.Tag is WorkItem ? "OnlyTaskOrBug" : "SelectTask"));
            return;
        }
        if (_call != null) { ShowMessage(Loc.T("CallInProgress")); return; }
        StartTask(item);
        UpdateUi();
    }

    /// <summary>Starts a task and makes it the current one. Other timers keep running only if the setting allows it.</summary>
    private void StartTask(WorkItem item)
    {
        _idlePausedTasks.Clear();
        _activeTask = item;
        if (!_settings.AllowMultipleTasks)
            foreach (var key in _running.Keys.Where(k => k != item.Key).ToList()) StopTask(key);
        ResumeTask(item);
        RememberLastTask(item.Key, running: true);
    }

    /// <summary>Restarts a timer that was paused (idle, call, sending time) without changing the current task.</summary>
    private void ResumeTask(WorkItem item)
    {
        if (!IsRunning(item.Key)) _running[item.Key] = (item, DateTime.UtcNow);
    }

    /// <summary>Stop button: stops the selected task if it is running, otherwise the only running one.</summary>
    private void StopByUser()
    {
        var key = SelectedItem is { } selected && IsRunning(selected.Key) ? selected.Key
            : _running.Count == 1 ? _running.Keys.First()
            : null;
        if (key == null)
        {
            ShowMessage(Loc.T("SelectRunningTask"));
            return;
        }

        _idlePausedTasks.Clear();
        StopTask(key);
        if (_activeTask?.Key == key) _activeTask = TaskRunning ? _running.Values.Last().Item : null;
        RememberLastTask(_settings.LastTaskKey, running: TaskRunning);
        // Once the time is recorded the stopped task is no longer selected, so it cannot be restarted by accident.
        _grid.ClearSelection();
        _grid.CurrentCell = null;
        UpdateUi();
    }

    private void RememberLastTask(string key, bool running)
    {
        if (_settings.LastTaskKey == key && _settings.LastTaskRunning == running) return;
        _settings.LastTaskKey = key;
        _settings.LastTaskRunning = running;
        SettingsStore.Save(_settings);
    }

    /// <summary>Stops one running task timer and records the elapsed segment in the local log.</summary>
    private void StopTask(string key)
    {
        if (!_running.Remove(key, out var run)) return;
        _log.Add(run.Item, EntryKind.Work, run.StartUtc, (DateTime.UtcNow - run.StartUtc).TotalSeconds);
    }

    private void StopAllTasks()
    {
        foreach (var key in _running.Keys.ToList()) StopTask(key);
    }

    // ---------- call / meeting ----------

    private void ToggleCall()
    {
        if (_call == null)
        {
            BeginCall(auto: false);
            return;
        }
        // Teams may still hold the microphone; don't let detection reopen the call we just closed.
        _suppressAutoCall = true;
        EndCall();
    }

    private void BeginCall(bool auto)
    {
        // Tasks paused for inactivity were still the ones being worked on, so they resume after the call too.
        var call = new CallSession { Auto = auto };
        call.ResumeTasks.AddRange(TaskRunning ? _running.Values.Select(r => r.Item) : _idlePausedTasks);
        _idlePausedTasks.Clear();
        StopAllTasks();
        _call = call;
        _offReadings = 0;
        UpdateUi();

        // A detected call is only asked about once it has lasted the minimum length (see OnTick), so
        // short calls never interrupt. A call started with the button is asked about at once.
        if (!auto || MinCallSeconds == 0) AskCallStart(call);
    }

    private void AskCallStart(CallSession call)
    {
        call.Asked = true;
        using var form = new AssignTimeForm(false, DateTime.UtcNow - call.StartUtc, TimeableItems, _activeTask, call.Kind, _activeTask);
        ShowModal(form);
        call.Kind = form.Kind;
        call.Target = form.Target;
        UpdateUi();
    }

    private void EndCall()
    {
        var call = _call;
        if (call == null) return;
        _call = null;

        var end = call.EndUtc ?? DateTime.UtcNow;
        var duration = end - call.StartUtc;

        // Too short to matter: no question, nothing recorded, and the paused tasks simply carry on.
        if (duration.TotalSeconds < MinCallSeconds)
        {
            foreach (var task in call.ResumeTasks) ResumeTask(task);
            SetStatus("CallIgnored", duration.ToString(@"m\:ss"));
            RefreshGrid();
            return;
        }

        using (var form = new AssignTimeForm(true, duration, TimeableItems, _activeTask, call.Kind, call.Target))
        {
            ShowModal(form);
            if (form.Target != null)
                _log.Add(form.Target, form.Kind, call.StartUtc, duration.TotalSeconds);
        }

        foreach (var task in call.ResumeTasks) ResumeTask(task);
        RefreshGrid();
    }

    private DialogResult ShowModal(Form form)
    {
        _dialogOpen = true;
        try { return form.ShowDialog(this); }
        finally { _dialogOpen = false; }
    }

    // ---------- dev machines ----------

    private void RebuildMachines()
    {
        _machineCombo.Items.Clear();
        _machineCombo.Items.AddRange(_settings.DevMachines
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Cast<object>().ToArray());
        _machineCombo.SelectedItem = _settings.DevMachines.FirstOrDefault(m => m.Id == _settings.SelectedMachineId)
                                     ?? _machineCombo.Items.Cast<DevMachine>().FirstOrDefault();
        // The selection may not have changed (same machine, edited details), so refresh explicitly.
        _ = RefreshMachineStatusAsync();
    }

    /// <summary>Power state of the selected machine; null while it is not known.</summary>
    private PowerState? _machineState;

    /// <summary>A machine that is starting or stopping is checked again after this long, until it settles.</summary>
    private const int BusyRecheckSeconds = 20;

    private void UpdateMachineButtons()
    {
        var machine = _machineCombo.SelectedItem as DevMachine;
        (_startMachineButton.Enabled, _stopMachineButton.Enabled, _connectButton.Enabled) =
            MachinesForm.PowerButtons(machine, _machineState);
        _stopMachineButton.Text = Loc.T(machine?.Location == MachineLocation.OnPremises ? "SaveMachine" : "StopMachine");
    }

    /// <param name="interactive">True when the user clicked the status: a Microsoft sign-in may be shown.</param>
    private async Task RefreshMachineStatusAsync(bool interactive = false)
    {
        var version = ++_machineStatusVersion;
        _machineState = null;
        UpdateMachineButtons();
        if (_machineCombo.SelectedItem is not DevMachine machine || !MachinePower.IsManaged(machine))
        {
            _machineStatusLabel.Text = _machineCombo.SelectedItem is DevMachine ? Loc.T("PowerNotManaged") : "";
            return;
        }
        _machineStatusLabel.Text = Loc.T("PowerChecking");
        var (state, text) = await MachinesForm.StatusAsync(machine, interactive, "PowerSignInMain");
        if (version != _machineStatusVersion || IsDisposed) return;
        _machineState = state;
        _machineStatusLabel.Text = text;
        UpdateMachineButtons();

        // Starting or stopping takes a few minutes; look again so the buttons follow without a click.
        if (state != PowerState.Busy) return;
        await Task.Delay(TimeSpan.FromSeconds(BusyRecheckSeconds));
        if (version == _machineStatusVersion && !IsDisposed) await RefreshMachineStatusAsync();
    }

    private async Task ChangeMachinePowerAsync(bool start)
    {
        if (_machineCombo.SelectedItem is not DevMachine machine) return;
        _startMachineButton.Enabled = _stopMachineButton.Enabled = _connectButton.Enabled = false;
        _machineStatusLabel.Text = Loc.T(start ? "PowerStarting" : machine.Location == MachineLocation.Cloud ? "PowerStopping" : "PowerSaving");
        try
        {
            await MachinesForm.ChangePowerAsync(this, machine, start);
        }
        finally
        {
            if (!IsDisposed) await RefreshMachineStatusAsync(interactive: true);
        }
    }

    // ---------- accounts ----------

    /// <summary>Sentinel entry at the end of the account list.</summary>
    private sealed record AddAccountOption
    {
        public override string ToString() => Loc.T("AddAccount");
    }

    private void RebuildAccounts()
    {
        _loadingAccounts = true;
        try
        {
            _accountCombo.Items.Clear();
            _accountCombo.Items.AddRange(_settings.Connections.Cast<object>().ToArray());
            _accountCombo.Items.Add(new AddAccountOption());
            _accountCombo.SelectedItem = _settings.ActiveConnection;
        }
        finally
        {
            _loadingAccounts = false;
        }
    }

    private async Task OnAccountChosenAsync()
    {
        if (_loadingAccounts) return;
        if (_accountCombo.SelectedItem is AddAccountOption)
        {
            await AddAccountAsync();
            return;
        }
        if (_accountCombo.SelectedItem is ConnectionProfile profile && profile.Id != _settings.ActiveConnectionId)
            if (SwitchAccount(profile)) await SyncAsync();
    }

    /// <summary>
    /// Makes another account the active one. Running timers are stopped first, so their time is saved
    /// under the account it was recorded in.
    /// </summary>
    private bool SwitchAccount(ConnectionProfile profile)
    {
        if (_call != null)
        {
            ShowMessage(Loc.T("SwitchDuringCall"));
            RebuildAccounts(); // put the selection back
            return false;
        }

        StopAllTasks();
        _idlePausedTasks.Clear();
        _activeTask = null;
        _settings.StoreActiveConnection();
        _settings.ActivateConnection(profile);
        _settings.LastTaskKey = "";
        _settings.LastTaskRunning = false;
        SettingsStore.Save(_settings);
        TimeLog.ActiveConnection = profile.Id;

        _items.Clear();
        _sprints.Clear();
        MergeLocalTasks();
        RebuildAccounts();
        RebuildFilters();
        RefreshGrid();
        return true;
    }

    private async Task AddAccountAsync()
    {
        var previous = _settings.ActiveConnection;
        var profile = new ConnectionProfile { Name = Loc.T("NewAccountName", _settings.Connections.Count + 1) };
        _settings.Connections.Add(profile);
        if (!SwitchAccount(profile))
        {
            _settings.Connections.Remove(profile);
            RebuildAccounts();
            return;
        }
        await OpenSettingsAsync(); // fill in the new account's address and token; syncs when saved

        // Cancelled before giving a token: drop the empty account and go back to the previous one.
        if (_settings.PatProtected.Length == 0 && previous != null && _settings.ActiveConnectionId == profile.Id && SwitchAccount(previous))
        {
            _settings.Connections.Remove(profile);
            SettingsStore.Save(_settings);
            RebuildAccounts();
            await SyncAsync();
        }
    }

    private async Task DeleteActiveAccountAsync()
    {
        if (_settings.Connections.Count < 2 || _settings.ActiveConnection is not { } current) return;
        var next = _settings.Connections.First(c => c.Id != current.Id);
        if (!SwitchAccount(next)) return;
        _settings.Connections.Remove(current);
        SettingsStore.Save(_settings);
        RebuildAccounts();
        await SyncAsync();
    }

    // ---------- pull requests ----------

    private void OpenPullRequests()
    {
        if (RepositoryProjects() is not { } projects) return;
        var linkable = _items.Where(i => i.Key.StartsWith("ado:", StringComparison.Ordinal)).ToList();
        using var form = new PullRequestsForm(_settings, projects, _grid.CurrentRow?.Tag as WorkItem, linkable);
        ShowModal(form);
    }

    private void OpenRepositories()
    {
        if (RepositoryProjects() is not { } projects) return;
        using var form = new RepositoriesForm(_settings, projects);
        ShowModal(form);
    }

    /// <summary>
    /// The Azure DevOps projects to look for repositories in: the configured project, or else every
    /// project the loaded tasks come from. GitHub needs none (an empty list). Null, after telling the
    /// user why, when repositories cannot be reached.
    /// </summary>
    private List<string>? RepositoryProjects()
    {
        if (!ProviderFactory.IsConfigured(_settings)) { ShowMessage(Loc.T("PrNeedsDevOps")); return null; }
        if (_settings.Provider == ProviderKind.GitHub) return new List<string>();
        var projects = _settings.Project.Length > 0
            ? new List<string> { _settings.Project }
            : _items.Where(i => !LocalTaskStore.IsLocal(i)).Select(i => i.Project).Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        if (projects.Count == 0) { ShowMessage(Loc.T("PrNoProjects"), MessageBoxIcon.Warning); return null; }
        return projects;
    }

    // ---------- add and edit tasks ----------

    /// <summary>Local tasks live beside the server items in the list, the filters and the timers.</summary>
    private void MergeLocalTasks()
    {
        _items.RemoveAll(LocalTaskStore.IsLocal);
        _items.AddRange(_localTasks.ToWorkItems());
        if (_activeTask != null && LocalTaskStore.IsLocal(_activeTask))
            _activeTask = _items.FirstOrDefault(i => i.Key == _activeTask.Key) ?? _activeTask;
    }

    private void AfterLocalChange()
    {
        MergeLocalTasks();
        RebuildFilters();
        RefreshGrid();
    }

    private bool ServerTasksAvailable =>
        ProviderFactory.IsConfigured(_settings) && _settings.Provider == ProviderKind.AzureDevOps;

    /// <summary>People seen as assignees in the list, plus the team accounts, for the "Assigned to" box.</summary>
    private List<Person> KnownPeople()
    {
        var people = _items.Where(i => !LocalTaskStore.IsLocal(i) && i.AssignedTo.Length > 0)
            .Select(i => new Person(i.AssignedTo, i.AssignedToAccount))
            .ToList();
        people.AddRange(_settings.TeamEmails.Select(e => new Person(e, e)));
        return people
            .GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Name != p.Account).First()) // prefer an entry with a real name
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A local task being moved to Azure DevOps, with the values from its edit form.</summary>
    private sealed record Transfer(WorkItem Item, LocalTask Local, string Title, string Description, double? RemainingWork);

    private async Task AddTaskAsync(Transfer? transfer = null)
    {
        var serverItems = _items.Where(i => !LocalTaskStore.IsLocal(i)).ToList();
        var projects = serverItems.Select(i => i.Project).Append(_settings.Project)
            .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        var options = new TaskEditOptions
        {
            ServerAvailable = ServerTasksAvailable,
            ForceServer = transfer != null,
            AllowAssign = ServerTasksAvailable,
            People = KnownPeople(),
            PrefillTitle = transfer?.Title ?? "",
            PrefillDescription = transfer?.Description ?? "",
            PrefillRemaining = transfer?.RemainingWork,
            // Tasks go under stories, bugs and the like, not under other tasks.
            Parents = serverItems.Where(i => !string.Equals(i.Type, "Task", StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Projects = projects,
            Sprints = _sprints.Select(s => s.Path).Concat(serverItems.Select(i => i.Sprint))
                .Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase).ToList(),
            DefaultProject = _settings.Project.Length > 0 ? _settings.Project : projects.FirstOrDefault() ?? "",
            DefaultSprint = _settings.SelectedSprint.Length > 0 ? _settings.SelectedSprint
                : _sprints.FirstOrDefault(s => s.IsCurrent)?.Path ?? "",
            Save = async form =>
            {
                if (form.IsLocal)
                {
                    var task = _localTasks.Add(new LocalTask
                    {
                        Title = form.TaskTitle, Customer = form.Customer, Description = form.Description,
                        RemainingWork = form.RemainingWork, DeliveryDate = form.DeliveryDate
                    });
                    AfterLocalChange();
                    SetStatus("TaskCreated", "L" + task.Number);
                    return;
                }

                using var provider = ProviderFactory.Create(_settings);
                var created = await provider.CreateTaskAsync(new NewTask(form.Project, form.TaskType, form.TaskTitle,
                    form.Description, form.Sprint, form.ParentId, form.RemainingWork, form.AssignedTo));
                _items.Add(created);
                if (transfer != null)
                {
                    MoveLocalTimeTo(transfer, created);
                    SetStatus("TaskMoved", created.Id);
                }
                else SetStatus("TaskCreated", created.Id);
                AfterLocalChange();
            }
        };

        using var form = new TaskEditForm(options);
        ShowModal(form);
    }

    /// <summary>
    /// After a local task was created in Azure DevOps: its unsent time, running timer and "last task"
    /// move to the new item, and the local task is removed.
    /// </summary>
    private void MoveLocalTimeTo(Transfer transfer, WorkItem created)
    {
        var oldKey = transfer.Item.Key;
        var wasRunning = IsRunning(oldKey);
        if (wasRunning) StopTask(oldKey);

        foreach (var entry in _log.Pending(oldKey))
        {
            entry.Key = created.Key;
            entry.Title = created.Title;
        }
        _log.Save();
        _localTasks.Remove(transfer.Local);

        if (_activeTask?.Key == oldKey) _activeTask = created;
        if (_settings.LastTaskKey == oldKey) RememberLastTask(created.Key, _settings.LastTaskRunning);
        if (wasRunning) ResumeTask(created);
    }

    private async Task EditTaskAsync(WorkItem item)
    {
        var local = _localTasks.Find(item);
        List<string> states;
        if (local != null)
            states = LocalTaskStore.States.ToList();
        else
        {
            if (!ProviderFactory.IsConfigured(_settings)) { ShowMessage(Loc.T("NotConfigured"), MessageBoxIcon.Warning); return; }
            try
            {
                using var provider = ProviderFactory.Create(_settings);
                states = await provider.GetStatesAsync(item);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                ShowMessage(ex.Message, MessageBoxIcon.Error);
                return;
            }
        }

        var options = new TaskEditOptions
        {
            Existing = item,
            ExistingLocal = local,
            States = states,
            AllowAssign = ServerTasksAvailable,
            People = KnownPeople(),
            CanTransfer = ServerTasksAvailable,
            Save = async form =>
            {
                if (local != null)
                {
                    if (form.Deleted) RemoveLocalTask(item, local);
                    else
                    {
                        local.Title = form.TaskTitle;
                        local.State = form.State ?? local.State;
                        local.RemainingWork = form.RemainingWork;
                        local.Customer = form.Customer;
                        local.DeliveryDate = form.DeliveryDate;
                        local.Description = form.Description;
                        _localTasks.Save();
                    }
                    AfterLocalChange();
                    return;
                }

                // Send only what changed, so an untouched field is never overwritten.
                var changes = new TaskChanges(
                    form.TaskTitle != item.Title ? form.TaskTitle : null,
                    form.State != null && !string.Equals(form.State, item.State, StringComparison.OrdinalIgnoreCase) ? form.State : null,
                    form.RemainingWork is { } remaining && Math.Abs(remaining - (item.RemainingWork ?? 0)) > 0.001 ? remaining : null,
                    form.AssigneeChanged ? form.AssignedTo ?? "" : null);
                using var provider = ProviderFactory.Create(_settings);
                await provider.UpdateTaskAsync(item, changes);
                RebuildFilters();
                RefreshGrid();
            }
        };

        Transfer? transfer = null;
        using (var form = new TaskEditForm(options))
        {
            ShowModal(form);
            if (form.TransferRequested && local != null)
                transfer = new Transfer(item, local, form.TaskTitle, form.Description, form.RemainingWork);
        }
        if (transfer != null) await AddTaskAsync(transfer);
    }

    /// <summary>Asks, then deletes a local task (from the row menu or the Delete key).</summary>
    private void DeleteLocalTask(WorkItem item)
    {
        if (_localTasks.Find(item) is not { } local) return;
        var answer = MessageBox.Show(this, Loc.T("ConfirmDeleteTask", item.Title), Loc.T("AppTitle"), MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, Loc.MsgOptions);
        if (answer != DialogResult.Yes) return;
        RemoveLocalTask(item, local);
        AfterLocalChange();
    }

    /// <summary>Removes a local task together with its running timer and unsent time.</summary>
    private void RemoveLocalTask(WorkItem item, LocalTask local)
    {
        StopTask(item.Key);
        _idlePausedTasks.RemoveAll(t => t.Key == item.Key);
        _call?.ResumeTasks.RemoveAll(t => t.Key == item.Key);
        _log.Update(new Dictionary<TimeEntry, double>(), _log.Pending(item.Key));
        _localTasks.Remove(local);
        if (_activeTask?.Key == item.Key) _activeTask = _running.Values.Select(r => r.Item).LastOrDefault();
    }

    /// <summary>Row menu shortcut: moves a local task to Azure DevOps.</summary>
    private async Task MoveLocalTaskAsync(WorkItem item)
    {
        if (_localTasks.Find(item) is not { } local) return;
        await AddTaskAsync(new Transfer(item, local, local.Title, local.Description, local.RemainingWork));
    }
    // ---------- correct unsent time ----------

    private void EditUnsentTime()
    {
        var item = SelectedItem;
        if (item == null) { ShowMessage(Loc.T("SelectTask")); return; }

        // Close the running stretch so it appears in the list, then keep timing afterwards.
        var wasRunning = IsRunning(item.Key);
        if (wasRunning) StopTask(item.Key);
        try
        {
            var pending = _log.Pending(item.Key);
            if (pending.Count == 0) { ShowMessage(Loc.T("NothingToPush")); return; }
            using var form = new EditTimeForm(item, pending, _log);
            ShowModal(form);
        }
        finally
        {
            if (wasRunning && _call == null && (_settings.AllowMultipleTasks || !TaskRunning)) ResumeTask(item);
            else if (wasRunning && _call != null && !_call.ResumeTasks.Contains(item)) _call.ResumeTasks.Add(item);
            RefreshGrid();
        }
    }

    // ---------- push time to the server ----------

    private async Task PushAsync()
    {
        var item = SelectedItem;
        if (item == null) { ShowMessage(Loc.T("SelectTask")); return; }
        if (LocalTaskStore.IsLocal(item)) { ShowMessage(Loc.T("LocalNoSend")); return; }
        if (!ProviderFactory.IsConfigured(_settings)) { ShowMessage(Loc.T("NotConfigured"), MessageBoxIcon.Warning); return; }

        // Close the running segment so it is included, then keep timing afterwards.
        var wasRunning = IsRunning(item.Key);
        if (wasRunning) StopTask(item.Key);

        _pushButton.Enabled = false;
        try
        {
            var pending = _log.Pending(item.Key);
            var hours = Math.Round(pending.Sum(e => e.Seconds) / 3600.0, 2);
            if (hours <= 0) { ShowMessage(Loc.T("NothingToPush")); return; }

            using var provider = ProviderFactory.Create(_settings);
            var states = await provider.GetStatesAsync(item);

            string? newState;
            bool reduceRemaining;
            using (var form = new SendTimeForm(item, hours, states, _settings.ReduceRemainingWork,
                       _settings.Provider == ProviderKind.AzureDevOps))
            {
                if (ShowModal(form) != DialogResult.OK) return;
                newState = form.NewState;
                reduceRemaining = form.ReduceRemaining;
            }

            await provider.LogTimeAsync(item, hours, newState, reduceRemaining);
            _log.MarkSynced(pending);
            SetStatus("Pushed", hours, item.Title);
            if (newState != null) RebuildFilters(); // the status filter may need the new value
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasRunning && _call == null && (_settings.AllowMultipleTasks || !TaskRunning)) ResumeTask(item);
            else if (wasRunning && _call != null && !_call.ResumeTasks.Contains(item)) _call.ResumeTasks.Add(item);
            _pushButton.Enabled = true;
            RefreshGrid();
        }
    }

    // ---------- ticking ----------

    private void OnTick()
    {
        _tick++;
        UpdateTimers();
        TrackWorkday();
        CheckIdle();

        // A detected call that has now lasted the minimum length gets its "call started" question.
        if (_call is { Asked: false, EndUtc: null } waiting && !_dialogOpen &&
            (DateTime.UtcNow - waiting.StartUtc).TotalSeconds >= MinCallSeconds)
            AskCallStart(waiting);

        if (_tick % AutoSyncEveryTicks == 0 && !_dialogOpen && _syncButton.Enabled)
            _ = SyncAsync(silent: true);

        if (_tick % DetectEveryTicks != 0 || !_settings.AutoDetectTeams) return;

        if (TeamsCallDetector.IsInCall())
        {
            _offReadings = 0;
            if (_call is { Auto: true }) _call.EndUtc = null;
            else if (_call == null && !_dialogOpen && !_suppressAutoCall) BeginCall(auto: true);
        }
        else
        {
            _suppressAutoCall = false;
            if (_call is not { Auto: true }) return;
            // Require two consecutive "off" readings so a brief device switch doesn't end the call,
            // but bill the call only up to the first one.
            _call.EndUtc ??= DateTime.UtcNow;
            if (++_offReadings >= 2 && !_dialogOpen) EndCall();
        }
    }

    /// <summary>Counts today's working time on the computer, whether or not a task is being timed.</summary>
    private void TrackWorkday()
    {
        // Real elapsed time, capped so a sleep/hibernate gap is not counted as work.
        var now = DateTime.UtcNow;
        var elapsed = Math.Min((now - _lastTickUtc).TotalSeconds, 5);
        _lastTickUtc = now;

        var limit = _settings.IdlePauseSeconds > 0 ? _settings.IdlePauseSeconds : DefaultActivityIdleSeconds;
        var working = !_session.IsAway && (_call != null || IdleDetector.IdleSeconds < limit);
        if (working) _workday.Add(elapsed, SessionMonitor.IsRemote);
        if (_tick % SaveActivityEveryTicks == 0) _workday.Save();

        var today = _workday.Today;
        var onTasks = _log.SecondsOn(DateTime.Now) + TotalRunningSeconds;
        var text = Loc.T("DaySummary", Format(today.ActiveSeconds), Format(onTasks), Format(today.RemoteSeconds));
        if (SessionMonitor.IsRemote) text += "  |  " + Loc.T("RemoteSession");
        if (_dayStatus.Text != text) _dayStatus.Text = text;
    }

    /// <summary>
    /// Pauses the task timer when the user is away (no input for the configured period, computer
    /// locked, or remote session disconnected) and resumes it when they are back.
    /// </summary>
    private void CheckIdle()
    {
        if (_call != null) return; // nobody types during a call; call time is handled separately
        var limit = _settings.IdlePauseSeconds;
        var idle = _session.IsAway || (limit > 0 && IdleDetector.IdleSeconds >= limit);

        if (idle && TaskRunning)
        {
            _idlePausedTasks = _running.Values.Select(r => r.Item).ToList();
            StopAllTasks();
            UpdateUi();
        }
        else if (!idle && _idlePausedTasks.Count > 0)
        {
            foreach (var task in _idlePausedTasks) ResumeTask(task);
            _idlePausedTasks.Clear();
            UpdateUi();
        }
    }

    private static string Format(double seconds) => TimeSpan.FromSeconds(Math.Floor(seconds)).ToString(@"hh\:mm\:ss");

    private void UpdateTimers()
    {
        UpdateClock();

        // The big timer follows the selected task while it is running, otherwise the current task.
        var shown = SelectedItem is { } selected && IsRunning(selected.Key) ? selected : _activeTask;
        var taskSeconds = shown == null ? 0 : _log.PendingSeconds(shown.Key) + RunningSeconds(shown.Key);
        _taskTime.Text = Format(taskSeconds);
        _taskTime.ForeColor = shown != null && IsRunning(shown.Key) ? Theme.Green : Theme.Muted;

        _callTime.Text = Format(_call == null ? 0 : ((_call.EndUtc ?? DateTime.UtcNow) - _call.StartUtc).TotalSeconds);
        _callTime.ForeColor = _call != null ? Theme.Violet : Theme.Muted;

        // Tooltip of the tray icon; Windows limits it to 127 characters.
        var tip = _call != null ? $"{Loc.T("AppTitle")} - {Loc.T("CallTime")} {_callTime.Text}"
            : TaskRunning ? $"{Loc.T("AppTitle")} ({_running.Count}) - {_taskTime.Text} - {shown}"
            : Loc.T("AppTitle");
        if (tip.Length > 120) tip = tip[..120];
        if (_tray.Text != tip) _tray.Text = tip;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not WorkItem item) continue;
            var seconds = _log.PendingSeconds(item.Key) + RunningSeconds(item.Key);

            // Running tasks stand out in green; rows that cannot be timed stay grey.
            var color = IsRunning(item.Key) ? Theme.Green : item.CanLogTime ? Color.Empty : Theme.Muted;
            if (row.DefaultCellStyle.ForeColor != color)
                row.DefaultCellStyle.ForeColor = row.DefaultCellStyle.SelectionForeColor = color;
            var text = seconds >= 1 ? Format(seconds) : "";
            var cell = row.Cells["ColUnsynced"];
            if (!Equals(cell.Value, text)) cell.Value = text;
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var hijri = Loc.T("HijriLabel", Loc.HijriDate(now));
        if (_hijriLabel.Text != hijri) _hijriLabel.Text = hijri;
        var gregorian = Loc.T("GregorianLabel", Loc.GregorianDate(now));
        if (_gregorianLabel.Text != gregorian) _gregorianLabel.Text = gregorian;
        _clockLabel.Text = Loc.T("TimeLabel", Loc.ClockTime(now));
    }

    private void UpdateButtons()
    {
        _startButton.Enabled = _call == null && SelectedItem is { } selected && !IsRunning(selected.Key);
        _stopButton.Enabled = TaskRunning;
        _editTimeButton.Enabled = SelectedItem != null;
        _pushButton.Enabled = SelectedItem is { } timeable && !LocalTaskStore.IsLocal(timeable);
        _editTaskButton.Enabled = _grid.CurrentRow?.Tag is WorkItem { IsListed: true };
    }

    private void UpdateUi()
    {
        _currentLabel.Text = _running.Count > 1
            ? Loc.T("CurrentTasks", string.Join("  |  ", _running.Values.Select(r => r.Item.ToString())))
            : Loc.T("CurrentTask", _activeTask?.ToString() ?? Loc.T("NoTask"));
        _callButton.Text = Loc.T(_call == null ? "StartCall" : "EndCall");
        if (_call != null) _status.Text = Loc.T("InCall");
        else if (TaskRunning) _status.Text = Loc.T("Running");
        else if (_idlePausedTasks.Count > 0) _status.Text = Loc.T(_session.IsAway ? "AwayPaused" : "IdlePaused");
        else _status.Text = Loc.T(_statusKey, _statusArgs);
        UpdateButtons();
        UpdateTimers();
    }

    /// <summary>Brings the window back from the notification area.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window only hides it: timers and detection keep running behind the tray icon.
        // Exit from the tray menu, Windows shutdown and the installer still close the app for real.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, Loc.T("AppTitle"), Loc.T("TrayStillRunning"), ToolTipIcon.Info);
            }
            return;
        }

        _tray.Visible = false;
        _tray.Dispose();
        _timer.Stop();
        if (_call != null)
        {
            _call.ResumeTasks.Clear();
            EndCall();
        }
        StopAllTasks();
        _workday.Save();
        _fileWatcher?.Dispose();
        _session.Dispose();
        base.OnFormClosing(e);
    }
}
