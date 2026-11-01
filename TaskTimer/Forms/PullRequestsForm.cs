using TaskTimer.Models;
using TaskTimer.Services;

namespace TaskTimer.Forms;

/// <summary>Lists open pull requests (Azure Repos or GitHub) and lets the user approve, complete or create them.</summary>
public sealed class PullRequestsForm : Form
{
    private readonly AppSettings _settings;
    private readonly PullRequestFeatures _features;
    private readonly IReadOnlyList<string> _projects;
    private readonly WorkItem? _currentTask;
    private readonly IReadOnlyList<WorkItem> _linkable;
    private List<PullRequest> _pullRequests = new();

    private readonly CheckBox _onlyMine = new() { AutoSize = true, Checked = true, Margin = new Padding(3, 8, 12, 3) };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private readonly Button _refresh = NewButton("PrRefresh", Theme.Neutral);
    private readonly Button _create = NewButton("PrNew", Theme.Green);
    private readonly Button _approve = NewButton("PrApprove", Theme.Primary);
    private readonly Button _complete = NewButton("PrComplete", Theme.Violet);
    private readonly Button _open = NewButton("OpenInBrowser", Theme.Neutral);
    private readonly Button _review = NewButton("PrReview", Theme.Primary);
    private readonly Button _workItems = NewButton("PrWorkItems", Theme.Neutral);
    private readonly Button _repoFilter = NewButton("PrRepoFilterAll", Theme.Neutral);
    private readonly Button _repositories = NewButton("Repositories", Theme.Neutral);

    /// <param name="projects">Azure DevOps projects to look in; not used for GitHub.</param>
    /// <param name="currentTask">Task selected in the main window, offered as the link for a new pull request.</param>
    /// <param name="linkable">Azure DevOps work items a new pull request can be linked to.</param>
    public PullRequestsForm(AppSettings settings, IReadOnlyList<string> projects, WorkItem? currentTask, IReadOnlyList<WorkItem> linkable)
    {
        _settings = settings;
        _features = PullRequestServices.Features(settings);
        _projects = projects;
        _currentTask = currentTask;
        _linkable = linkable;

        Icon = Program.AppIcon;
        Text = Loc.T("PrTitle");
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1100, 520);
        MinimumSize = new Size(700, 360);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleGrid(_grid);

        foreach (var key in new[] { "PrColId", "PrColTitle", "PrColRepo", "PrColBranches", "PrColAuthor", "PrColReviewers", "PrColMyVote", "PrColChecks", "PrColCreated" })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = Loc.T(key), SortMode = DataGridViewColumnSortMode.Automatic });
        _grid.Columns["PrColId"]!.FillWeight = 35;
        _grid.Columns["PrColTitle"]!.FillWeight = 220;
        _grid.Columns["PrColMyVote"]!.FillWeight = 60;
        _grid.Columns["PrColCreated"]!.FillWeight = 70;

        _onlyMine.Text = Loc.T("PrOnlyMine");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 0) };
        top.Controls.AddRange(new Control[] { _onlyMine, _repoFilter, _refresh, _status, _blocked });
        UpdateRepoFilterText();

        var close = NewButton("MachineClose", Theme.Neutral);
        close.DialogResult = DialogResult.OK;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { _create, _review, _workItems, _approve, _complete, _open, _repositories, close });
        // Only Azure DevOps links pull requests to work items.
        _workItems.Visible = _features.WorkItems;

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);
        CancelButton = close;

        _onlyMine.CheckedChanged += (_, _) => ShowList();
        _refresh.Click += async (_, _) => await LoadAsync();
        _create.Click += async (_, _) => await CreateAsync();
        _approve.Click += async (_, _) => await ApproveAsync();
        _complete.Click += async (_, _) => await CompleteAsync();
        _open.Click += (_, _) => { if (Selected is { } pr) OpenInBrowser(pr.WebUrl); };
        _review.Click += async (_, _) => await ReviewAsync();
        _workItems.Click += (_, _) => { if (Selected is { } pr) LinkedWorkItemsForm.Show(this, _settings, pr); };
        _repoFilter.Click += async (_, _) => await ChooseRepositoriesAsync();
        _repositories.Click += async (_, _) =>
        {
            using (var form = new RepositoriesForm(_settings, _projects)) form.ShowDialog(this);
            await LoadAsync(); // repositories or branches may be gone
        };
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await ReviewAsync(); };
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        Shown += async (_, _) => await LoadAsync();
    }

    private static Button NewButton(string key, Color color)
    {
        var button = new Button { Text = Loc.T(key), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(button, color);
        return button;
    }

    private PullRequest? Selected => _grid.CurrentRow?.Tag as PullRequest;

    /// <summary>Why the selected pull request cannot be approved or completed, shown next to the count.</summary>
    private readonly Label _blocked = new() { AutoSize = true, Margin = new Padding(24, 8, 3, 3), ForeColor = Theme.Coral };

    /// <summary>Pull requests whose checks could not be read; they are not held back by checks.</summary>
    private readonly HashSet<string> _checksUnavailable = new();

    /// <summary>Required checks that failed, or null while they are still being fetched.</summary>
    private List<PolicyCheck>? FailedChecks(PullRequest pr) =>
        _checks.TryGetValue(pr.Key, out var checks) ? checks.Where(c => c.Failed && c.IsBlocking).ToList()
        : _checksUnavailable.Contains(pr.Key) ? new List<PolicyCheck>()
        : null;

    private void UpdateRepoFilterText() =>
        _repoFilter.Text = _settings.PullRequestRepositories.Count == 0
            ? Loc.T(_features.UsesProjects ? "PrRepoFilterAll" : "PrRepoFilterInvolved")
            : Loc.T("PrRepoFilterSome", _settings.PullRequestRepositories.Count);

    /// <summary>Lets the user tick the repositories to show; saved with the account.</summary>
    private async Task ChooseRepositoriesAsync()
    {
        List<GitRepository> repositories = new();
        var loaded = await RunAsync(async service =>
        {
            foreach (var project in _features.UsesProjects ? _projects : new[] { "" })
                repositories.AddRange(await service.GetRepositoriesAsync(project));
        });
        if (!loaded || IsDisposed) return;

        using var form = new RepositoryFilterForm(repositories, _settings.PullRequestRepositories, _features.UsesProjects);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _settings.PullRequestRepositories = form.Chosen.ToList();
        SettingsStore.Save(_settings);
        UpdateRepoFilterText();
        await LoadAsync();
    }

    /// <summary>
    /// Approving and completing are allowed only once the checks are known and no required one has
    /// failed; completing also needs a draft-free, conflict-free pull request.
    /// </summary>
    private void UpdateButtons()
    {
        var pr = Selected;
        var failed = pr == null ? null : FailedChecks(pr);
        var checksOk = failed is { Count: 0 };
        var conflicts = pr?.MergeStatus == "conflicts";
        _approve.Enabled = pr != null && pr.MyVote != PullRequestService.VoteApproved && checksOk;
        _complete.Enabled = pr is { IsDraft: false } && checksOk && !conflicts;
        _open.Enabled = _review.Enabled = _workItems.Enabled = pr != null;

        _blocked.Text = pr == null ? ""
            : failed == null ? Loc.T("PrChecksPending")
            : failed.Count > 0 ? Loc.T("PrBlockedChecks", string.Join(", ", failed.Select(c => c.Name)))
            : conflicts ? Loc.T("PrBlockedConflicts")
            : "";
    }

    private async Task<bool> RunAsync(Func<IPullRequestService, Task> action)
    {
        UseWaitCursor = true;
        foreach (var button in new[] { _refresh, _create, _approve, _complete, _repoFilter }) button.Enabled = false;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            await action(service);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            if (!IsDisposed)
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Loc.MsgOptions);
            return false;
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                _refresh.Enabled = _create.Enabled = _repoFilter.Enabled = true;
                UpdateButtons();
            }
        }
    }

    private async Task LoadAsync()
    {
        _status.Text = Loc.T("PrLoading");
        // Refresh fetches the checks again; answers still on their way from before are ignored.
        _checks.Clear();
        _checksUnavailable.Clear();
        _checksVersion++;
        await RunAsync(async service =>
        {
            _pullRequests = await service.GetActiveAsync(_projects, _settings.PullRequestRepositories);
            _myId = await service.GetMyIdAsync();
        });
        if (!IsDisposed) ShowList();
    }

    private void ShowList()
    {
        var selectedKey = Selected?.Key;
        var shown = _onlyMine.Checked
            ? _pullRequests.Where(p => p.IAmReviewer || p.CreatedById == _myId).ToList()
            : _pullRequests;
        _grid.Rows.Clear();
        foreach (var pr in shown)
        {
            var title = pr.IsDraft ? Loc.T("PrDraft") + " " + pr.Title : pr.Title;
            var index = _grid.Rows.Add(pr.Id, title, pr.RepositoryName, $"{pr.SourceBranch} → {pr.TargetBranch}", pr.CreatedBy,
                pr.Reviewers, VoteText(pr.MyVote, pr.IAmReviewer), "…", pr.Created?.ToString("yyyy-MM-dd") ?? "");
            _grid.Rows[index].Tag = pr;
            if (_checks.TryGetValue(pr.Key, out var known)) ShowChecks(_grid.Rows[index], pr, known);
            if (pr.Key == selectedKey) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }
        _status.Text = Loc.T("PrCount", shown.Count);
        UpdateButtons();
        _ = LoadChecksAsync(shown.Where(p => !_checks.ContainsKey(p.Key)).ToList());
    }

    /// <summary>Checks per pull request key; kept until the next Refresh.</summary>
    private readonly Dictionary<string, List<PolicyCheck>> _checks = new();
    private int _checksVersion;

    /// <summary>Fetches the checks of the listed pull requests a few at a time; each row fills in when its answer arrives.</summary>
    private async Task LoadChecksAsync(List<PullRequest> pullRequests)
    {
        var version = _checksVersion;
        using var service = PullRequestServices.Create(_settings);
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(pullRequests.Select(async pr =>
        {
            await gate.WaitAsync();
            try
            {
                var checks = await service.GetChecksAsync(pr);
                if (version != _checksVersion || IsDisposed) return;
                _checks[pr.Key] = checks;
                if (RowOf(pr.Key) is { } row) ShowChecks(row, pr, checks);
                if (Selected?.Key == pr.Key) UpdateButtons();
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                if (!IsDisposed && RowOf(pr.Key) is { } row)
                {
                    row.Cells["PrColChecks"].Value = Loc.T("PrChecksUnknown");
                    row.Cells["PrColChecks"].ToolTipText = ex.Message;
                }
                if (!IsDisposed)
                {
                    _checksUnavailable.Add(pr.Key);
                    if (Selected?.Key == pr.Key) UpdateButtons();
                }
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private DataGridViewRow? RowOf(string key) =>
        _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as PullRequest)?.Key == key);

    /// <summary>One-line summary in the Checks column, coloured by outcome, with every check listed in its tooltip.</summary>
    private static void ShowChecks(DataGridViewRow row, PullRequest pr, List<PolicyCheck> checks)
    {
        var failed = checks.Count(c => c.Failed && c.IsBlocking);
        var pending = checks.Count(c => c.Pending);
        var passed = checks.Count(c => c.Passed);
        var conflicts = pr.MergeStatus == "conflicts";

        var summary = failed > 0 ? Loc.T("PrChecksFailed", failed)
            : pending > 0 ? Loc.T("PrChecksRunning", pending)
            : checks.Count > 0 ? Loc.T("PrChecksPassed", passed, checks.Count)
            : Loc.T("PrChecksNone");
        if (conflicts) summary = Loc.T("PrConflicts") + "   " + summary;

        var cell = row.Cells["PrColChecks"];
        cell.Value = summary;
        cell.Style.ForeColor = failed > 0 || conflicts ? Theme.Coral : pending > 0 ? Theme.Violet : checks.Count > 0 ? Theme.Green : Theme.Muted;
        cell.ToolTipText = string.Join(Environment.NewLine, checks.Select(c =>
            $"{(c.Passed ? "✔" : c.Failed ? "✖" : c.Pending ? "⏳" : "•")} {c.Name} ({Loc.T(c.IsBlocking ? "PrCheckRequired" : "PrCheckOptional")})"));
        if (conflicts) cell.ToolTipText = Loc.T("PrConflicts") + Environment.NewLine + cell.ToolTipText;
    }

    /// <summary>The signed-in user's ID, to pick out pull requests they created or review.</summary>
    private string _myId = "";

    private static string VoteText(int vote, bool isReviewer) => vote switch
    {
        10 => Loc.T("PrVoteApproved"),
        5 => Loc.T("PrVoteSuggestions"),
        -5 => Loc.T("PrVoteWaiting"),
        -10 => Loc.T("PrVoteRejected"),
        _ => isReviewer ? Loc.T("PrVoteNone") : ""
    };

    private async Task ReviewAsync()
    {
        if (Selected is not { } pr) return;
        // The review window follows the same rule: no approving while a required check fails.
        var failed = FailedChecks(pr);
        if (failed == null)
        {
            await RunAsync(async service => _checks[pr.Key] = await service.GetChecksAsync(pr));
            failed = FailedChecks(pr) ?? new List<PolicyCheck>();
        }
        var reason = failed.Count > 0 ? Loc.T("PrBlockedChecks", string.Join(", ", failed.Select(c => c.Name))) : null;
        using (var form = new PullRequestReviewForm(_settings, pr, reason)) form.ShowDialog(this);
        await LoadAsync(); // votes and comments may have changed
    }

    private async Task ApproveAsync()
    {
        if (Selected is not { } pr || !_approve.Enabled) return;
        if (await RunAsync(service => service.ApproveAsync(pr))) await LoadAsync();
    }

    private async Task CompleteAsync()
    {
        if (Selected is not { } pr || !_complete.Enabled) return;
        using var form = new CompletePullRequestForm(pr, _features);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        if (await RunAsync(service => service.CompleteAsync(pr, form.Options))) await LoadAsync();
    }

    private async Task CreateAsync()
    {
        using var form = new NewPullRequestForm(_settings, _projects, _currentTask, _linkable);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        await LoadAsync();
        if (RowOf(form.CreatedKey) is { } created) _grid.CurrentCell = created.Cells[0];
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Loc.MsgOptions);
        }
    }
}

/// <summary>Asks how to complete (merge) a pull request.</summary>
public sealed class CompletePullRequestForm : Form
{
    private static readonly Dictionary<string, string> StrategyText = new()
    {
        ["noFastForward"] = "PrMergeNoFf", ["squash"] = "PrMergeSquash", ["rebase"] = "PrMergeRebase", ["rebaseMerge"] = "PrMergeSemiLinear"
    };

    private readonly IReadOnlyList<string> _strategies;
    private readonly ComboBox _strategy = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly CheckBox _deleteBranch = new() { AutoSize = true, Checked = true };
    private readonly CheckBox _completeItems = new() { AutoSize = true };
    private readonly TextBox _message = new() { Multiline = true, Width = 460, Height = 70, ScrollBars = ScrollBars.Vertical };

    public PullRequestCompletion Options => new(_strategies[Math.Max(0, _strategy.SelectedIndex)], _deleteBranch.Checked,
        _completeItems.Checked, _message.Text.Trim());

    public CompletePullRequestForm(PullRequest pr, PullRequestFeatures features)
    {
        _strategies = features.MergeStrategies;
        _completeItems.Visible = features.WorkItems;
        Icon = Program.AppIcon;
        Text = Loc.T("PrComplete");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);

        _strategy.Items.AddRange(_strategies.Select(s => (object)Loc.T(StrategyText[s])).ToArray());
        _strategy.SelectedIndex = 0;
        _deleteBranch.Text = Loc.T("PrDeleteBranch");
        _completeItems.Text = Loc.T("PrCompleteItems");
        _message.Text = $"Merged PR {pr.Id}: {pr.Title}";

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14), Dock = DockStyle.Fill
        };
        layout.Controls.Add(new Label { Text = $"{pr.Id} - {pr.Title}", AutoSize = true, MaximumSize = new Size(470, 0), Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10) });
        layout.Controls.Add(new Label { Text = Loc.T("PrMergeType"), AutoSize = true });
        layout.Controls.Add(_strategy);
        layout.Controls.Add(new Label { Text = Loc.T("PrMergeMessage"), AutoSize = true, Margin = new Padding(3, 10, 3, 3) });
        layout.Controls.Add(_message);
        layout.Controls.Add(_deleteBranch);
        layout.Controls.Add(_completeItems);

        var ok = new Button { Text = Loc.T("PrComplete"), AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(12, 3, 12, 3) };
        var cancel = new Button { Text = Loc.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(ok, Theme.Violet);
        Theme.StyleButton(cancel, Theme.Neutral);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Creates a pull request in an Azure Repos or GitHub repository, optionally linked to the current task (Azure DevOps).</summary>
public sealed class NewPullRequestForm : Form
{
    private readonly AppSettings _settings;
    private readonly PullRequestFeatures _features;
    private readonly WorkItem? _task;
    /// <summary>GitHub: the current issue's repository ("owner/name"), chosen first.</summary>
    private readonly string _preferredRepository;
    private readonly ComboBox _project = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly ComboBox _repository = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 360, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 360, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly TextBox _title = new() { Dock = DockStyle.Fill };
    private readonly TextBox _description = new() { Dock = DockStyle.Fill, Multiline = true, Height = 90, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox _draft = new() { AutoSize = true };
    private readonly CheckedListBox _links = new() { Dock = DockStyle.Fill, Height = 170, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _linkSearch = new() { Width = 280 };
    private readonly Button _linkSearchButton = new() { AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
    private readonly Label _linkHint = new() { AutoSize = true, Margin = new Padding(8, 6, 3, 3) };

    /// <summary>A work item that can be linked: loaded in the main window, or found by searching Azure DevOps.</summary>
    private sealed record LinkOption(string Id, string Type, string Title)
    {
        public override string ToString() => $"{Type} {Id} - {Title}";
    }

    /// <summary>Every work item known to the list (the search shows part of it), and the ticked ones.</summary>
    private readonly List<LinkOption> _allLinks = new();
    private readonly HashSet<string> _checkedIds = new();
    private bool _fillingLinks;
    private readonly Button _createButton = new() { AutoSize = true, Padding = new Padding(12, 3, 12, 3) };
    private int _loadVersion;

    /// <summary>The new pull request's <see cref="PullRequest.Key"/>.</summary>
    public string CreatedKey { get; private set; } = "";

    public NewPullRequestForm(AppSettings settings, IReadOnlyList<string> projects, WorkItem? task, IReadOnlyList<WorkItem> linkable)
    {
        _settings = settings;
        _features = PullRequestServices.Features(settings);
        // Only an Azure DevOps work item can be linked; local and GitHub items cannot.
        _task = task != null && task.Key.StartsWith("ado:", StringComparison.Ordinal) ? task : null;
        _preferredRepository = task != null && task.Key.StartsWith("gh:", StringComparison.Ordinal) ? task.Project : "";

        Icon = Program.AppIcon;
        Text = Loc.T("PrNew");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(700, 640);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // Returns the label, so a row that does not apply to the provider can be hidden (it then takes no space).
        Label Row(string key, Control field)
        {
            var label = new Label { Text = Loc.T(key), AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 8, 12, 8) };
            table.Controls.Add(label);
            table.Controls.Add(field);
            return label;
        }
        var projectLabel = Row("TaskProject", _project);
        projectLabel.Visible = _project.Visible = _features.UsesProjects;
        Row("PrRepository", _repository);
        Row("PrSource", _source);
        Row("PrTarget", _target);
        Row("TaskTitle", _title);
        Row("TaskDescription", _description);
        _draft.Text = Loc.T("PrCreateDraft");
        table.Controls.Add(new Label());
        table.Controls.Add(_draft);
        // Tasks, bugs, user stories, change requests...: anything can be linked, several at once.
        foreach (var item in linkable) _allLinks.Add(new LinkOption(item.Id, item.Type, item.Title));
        if (_task != null) _checkedIds.Add(_task.Id);
        _linkSearch.PlaceholderText = Loc.T("PrLinkSearch");
        _linkSearchButton.Text = Loc.T("PrSearchDevOps");
        _linkSearch.TextChanged += (_, _) => FillLinks();
        _linkSearch.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; // Enter searches instead of creating the pull request
            await SearchServerAsync();
        };
        _linkSearchButton.Click += async (_, _) => await SearchServerAsync();
        _links.ItemCheck += (_, e) =>
        {
            if (_fillingLinks || _links.Items[e.Index] is not LinkOption option) return;
            if (e.NewValue == CheckState.Checked) _checkedIds.Add(option.Id);
            else _checkedIds.Remove(option.Id);
            UpdateLinkHint();
        };
        var searchPanel = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        searchPanel.Controls.AddRange(new Control[] { _linkSearch, _linkSearchButton, _linkHint });
        var linksLabel = Row("PrLinks", searchPanel);
        table.Controls.Add(new Label());
        table.Controls.Add(_links);
        linksLabel.Visible = searchPanel.Visible = _links.Visible = _features.WorkItems;
        FillLinks();

        _createButton.Text = Loc.T("PrCreate");
        var cancel = new Button { Text = Loc.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(_createButton, Theme.Green);
        Theme.StyleButton(cancel, Theme.Neutral);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { cancel, _createButton });
        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = _createButton;
        CancelButton = cancel;

        _project.Items.AddRange(projects.Cast<object>().ToArray());
        _project.SelectedIndexChanged += async (_, _) => await LoadRepositoriesAsync();
        _repository.SelectedIndexChanged += async (_, _) => await LoadBranchesAsync();
        _createButton.Click += async (_, _) => await CreateAsync();
        // A pull request for a task is usually named after it.
        if (_task != null) _title.Text = _task.Title;
        if (!_features.UsesProjects) ClientSize = new Size(700, 420); // no project and no work item links
        Shown += async (_, _) =>
        {
            if (!_features.UsesProjects)
            {
                await LoadRepositoriesAsync();
                return;
            }
            var preferred = _task?.Project ?? projects.FirstOrDefault();
            _project.SelectedItem = projects.FirstOrDefault(p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase));
            if (_project.SelectedIndex < 0 && _project.Items.Count > 0) _project.SelectedIndex = 0;
        };
    }

    /// <summary>Lists the ticked items first, then the others that match the search text (ID, title or type).</summary>
    private void FillLinks()
    {
        var text = _linkSearch.Text.Trim().TrimStart('#');
        bool Matches(LinkOption o) => text.Length == 0 ||
                                      o.Id.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                      o.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                      o.Type.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        var shown = _allLinks.Where(o => _checkedIds.Contains(o.Id))
            .Concat(_allLinks.Where(o => !_checkedIds.Contains(o.Id) && Matches(o))
                .OrderBy(o => o.Type).ThenBy(o => o.Title, StringComparer.CurrentCultureIgnoreCase))
            .ToList();

        _fillingLinks = true;
        _links.BeginUpdate();
        _links.Items.Clear();
        foreach (var option in shown) _links.Items.Add(option, _checkedIds.Contains(option.Id));
        _links.EndUpdate();
        _fillingLinks = false;
        UpdateLinkHint();
    }

    private void UpdateLinkHint() => _linkHint.Text = Loc.T("PrLinksSelected", _checkedIds.Count);

    /// <summary>Finds work items in Azure DevOps by ID or title, for ones not loaded in the main window.</summary>
    private async Task SearchServerAsync()
    {
        var text = _linkSearch.Text.Trim().TrimStart('#');
        if (text.Length == 0 || _project.SelectedItem is not string project) return;
        _linkSearchButton.Enabled = false;
        UseWaitCursor = true;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            var found = await service.SearchWorkItemsAsync(project, text);
            if (IsDisposed) return;
            foreach (var item in found.Where(f => _allLinks.All(o => o.Id != f.Id)))
                _allLinks.Add(new LinkOption(item.Id, item.Type, item.Title));
            FillLinks();
            if (found.Count == 0) _linkHint.Text = Loc.T("PrSearchNothing");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex);
        }
        finally
        {
            UseWaitCursor = false;
            _linkSearchButton.Enabled = true;
        }
    }

    private void ShowError(Exception ex) =>
        MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Loc.MsgOptions);

    /// <summary>The chosen team project (Azure DevOps), "" for GitHub, or null when none is chosen yet.</summary>
    private string? CurrentProject => _features.UsesProjects ? _project.SelectedItem as string : "";

    private async Task LoadRepositoriesAsync()
    {
        if (CurrentProject is not { } project) return;
        var version = ++_loadVersion;
        _repository.Items.Clear();
        _source.Items.Clear();
        _target.Items.Clear();
        UseWaitCursor = true;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            var repositories = await service.GetRepositoriesAsync(project);
            if (version != _loadVersion || IsDisposed) return;
            _repository.Items.AddRange(repositories.Cast<object>().ToArray());
            _repository.SelectedItem = repositories.FirstOrDefault(r => string.Equals(r.Id, _preferredRepository, StringComparison.OrdinalIgnoreCase));
            if (_repository.SelectedIndex < 0 && _repository.Items.Count > 0) _repository.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex);
        }
        finally
        {
            if (!IsDisposed) UseWaitCursor = false;
        }
    }

    private async Task LoadBranchesAsync()
    {
        if (CurrentProject is not { } project || _repository.SelectedItem is not GitRepository repository) return;
        var version = ++_loadVersion;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            var branches = await service.GetBranchesAsync(project, repository.Id);
            if (version != _loadVersion || IsDisposed) return;
            _source.Items.Clear();
            _target.Items.Clear();
            _source.Items.AddRange(branches.Cast<object>().ToArray());
            _target.Items.AddRange(branches.Cast<object>().ToArray());
            _target.Text = repository.DefaultBranch;
            _source.Text = branches.FirstOrDefault(b => b != repository.DefaultBranch) ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex);
        }
    }

    private async Task CreateAsync()
    {
        var source = _source.Text.Trim();
        var target = _target.Text.Trim();
        string? problem = _repository.SelectedItem is not GitRepository ? "PrChooseRepository"
            : source.Length == 0 || target.Length == 0 ? "PrChooseBranches"
            : string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ? "PrSameBranch"
            : _title.Text.Trim().Length == 0 ? "TitleRequired"
            : null;
        if (problem != null)
        {
            MessageBox.Show(this, Loc.T(problem), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, Loc.MsgOptions);
            return;
        }

        _createButton.Enabled = false;
        UseWaitCursor = true;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            var repository = (GitRepository)_repository.SelectedItem!;
            var (id, _) = await service.CreateAsync(CurrentProject ?? "", repository,
                source, target, _title.Text.Trim(), _description.Text.Trim(), _draft.Checked,
                _checkedIds.ToList());
            CreatedKey = $"{repository.Id}#{id}";
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex);
        }
        finally
        {
            UseWaitCursor = false;
            _createButton.Enabled = true;
        }
    }
}
