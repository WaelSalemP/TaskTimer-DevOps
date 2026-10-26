using TaskTimer.Models;
using TaskTimer.Services;

namespace TaskTimer.Forms;

/// <summary>
/// Manages the account's repositories (Azure Repos or GitHub): create, rename and delete them, and
/// create and delete their branches.
/// </summary>
public sealed class RepositoriesForm : Form
{
    private readonly AppSettings _settings;
    private readonly PullRequestFeatures _features;
    private readonly IReadOnlyList<string> _projects;
    private List<GitRepository> _repositories = new();
    private List<string> _branches = new();
    private int _branchVersion;

    private readonly TextBox _search = new() { Width = 260, Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    /// <summary>Why the selected repository cannot be deleted, when it cannot.</summary>
    private readonly Label _permissionNote = new() { AutoSize = true, Margin = new Padding(24, 8, 3, 3), ForeColor = Theme.Muted };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private readonly ListBox _branchList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Label _branchesLabel = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(2, 4, 2, 4) };
    private readonly Button _refresh = NewButton("PrRefresh", Theme.Neutral);
    private readonly Button _newRepository = NewButton("RepoNew", Theme.Green);
    private readonly Button _rename = NewButton("RepoRename", Theme.Neutral);
    private readonly Button _delete = NewButton("RepoDelete", Theme.Coral);
    private readonly Button _open = NewButton("OpenInBrowser", Theme.Neutral);
    private readonly Button _newBranch = NewButton("BranchNew", Theme.Green);
    private readonly Button _deleteBranch = NewButton("BranchDelete", Theme.Coral);

    /// <param name="projects">Azure DevOps projects whose repositories are shown; not used for GitHub.</param>
    public RepositoriesForm(AppSettings settings, IReadOnlyList<string> projects)
    {
        _settings = settings;
        _features = PullRequestServices.Features(settings);
        _projects = projects;

        Icon = Program.AppIcon;
        Text = Loc.T("RepoTitle");
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1100, 560);
        MinimumSize = new Size(760, 380);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleGrid(_grid);

        foreach (var (key, weight) in new[] { ("RepoColName", 200), ("RepoColProject", 110), ("RepoColDefault", 90), ("RepoColVisibility", 60) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = Loc.T(key), FillWeight = weight });
        _grid.Columns["RepoColProject"]!.HeaderText = Loc.T(_features.UsesProjects ? "RepoColProject" : "RepoColOwner");
        _grid.Columns["RepoColVisibility"]!.Visible = _features.PrivateRepositories;

        _search.PlaceholderText = Loc.T("RepoSearch");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 0) };
        top.Controls.AddRange(new Control[] { _search, _refresh, _status, _permissionNote });

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        Load += (_, _) => split.SplitterDistance = Math.Max(200, split.Width - LogicalToDeviceUnits(320));
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_branchList);
        split.Panel2.Controls.Add(_branchesLabel);
        split.Panel2.Padding = new Padding(6, 0, 0, 0);

        var close = NewButton("MachineClose", Theme.Neutral);
        close.DialogResult = DialogResult.OK;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        _newBranch.Margin = new Padding(24, 3, 3, 3); // set the branch buttons apart from the repository ones
        bottom.Controls.AddRange(new Control[] { _newRepository, _rename, _delete, _open, _newBranch, _deleteBranch, close });

        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(bottom);
        CancelButton = close;

        _search.TextChanged += (_, _) => ShowList();
        _refresh.Click += async (_, _) => await LoadAsync();
        _grid.SelectionChanged += async (_, _) => await LoadBranchesAsync();
        _newRepository.Click += async (_, _) => await CreateRepositoryAsync();
        _rename.Click += async (_, _) => await RenameRepositoryAsync();
        _delete.Click += async (_, _) => await DeleteRepositoryAsync();
        _open.Click += (_, _) => { if (Selected is { WebUrl.Length: > 0 } r) OpenInBrowser(r.WebUrl); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && Selected is { WebUrl.Length: > 0 } r) OpenInBrowser(r.WebUrl); };
        _newBranch.Click += async (_, _) => await CreateBranchAsync();
        _deleteBranch.Click += async (_, _) => await DeleteBranchAsync();
        _branchList.SelectedIndexChanged += (_, _) => UpdateButtons();
        Shown += async (_, _) => await LoadAsync();
        UpdateButtons();
    }

    private static Button NewButton(string key, Color color)
    {
        var button = new Button { Text = Loc.T(key), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(button, color);
        return button;
    }

    private GitRepository? Selected => _grid.CurrentRow?.Tag as GitRepository;

    private void UpdateButtons()
    {
        var repository = Selected;
        _newBranch.Enabled = repository != null;
        // Only offered where the account has the right; the server still checks when it is tried.
        _rename.Enabled = repository is { CanRename: true };
        _delete.Enabled = repository is { CanDelete: true };
        _permissionNote.Text = repository == null || repository.CanDelete ? ""
            : Loc.T(repository.DeleteNeedsSignIn ? "RepoDeleteNeedsSignIn" : "RepoNoDeleteRight");
        _open.Enabled = repository is { WebUrl.Length: > 0 };
        // The default branch cannot be deleted: pull requests and clones depend on it.
        _deleteBranch.Enabled = repository != null && _branchList.SelectedItem is string branch && branch != repository.DefaultBranch;
    }

    private void ShowError(Exception ex, string? hint = null) =>
        MessageBox.Show(this, hint == null ? ex.Message : ex.Message + Environment.NewLine + Environment.NewLine + hint, Text,
            MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Loc.MsgOptions);

    /// <summary>Runs a server call with the wait cursor; errors are shown and false is returned.</summary>
    private async Task<bool> RunAsync(Func<IPullRequestService, Task> action, Func<Exception, string?>? hint = null)
    {
        UseWaitCursor = true;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            await action(service);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            if (!IsDisposed) ShowError(ex, hint?.Invoke(ex));
            return false;
        }
        finally
        {
            if (!IsDisposed) UseWaitCursor = false;
        }
    }

    private async Task LoadAsync(string? select = null)
    {
        select ??= Selected?.Id;
        _status.Text = Loc.T("PrLoading");
        var loaded = new List<GitRepository>();
        await RunAsync(async service =>
        {
            foreach (var project in _features.UsesProjects ? _projects : new[] { "" })
                loaded.AddRange(await service.GetRepositoriesAsync(project));
        });
        if (IsDisposed) return;
        _repositories = loaded.OrderBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ShowList(select);
    }

    private void ShowList(string? select = null)
    {
        select ??= Selected?.Id;
        var text = _search.Text.Trim();
        var shown = _repositories.Where(r => text.Length == 0 ||
                                             r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                             r.Project.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        _grid.Rows.Clear();
        foreach (var repository in shown)
        {
            var index = _grid.Rows.Add(repository.Name, repository.Project, repository.DefaultBranch, VisibilityText(repository.Visibility));
            _grid.Rows[index].Tag = repository;
            if (repository.Id == select) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }
        _status.Text = Loc.T("RepoCount", shown.Count);
        UpdateButtons();
    }

    private static string VisibilityText(string visibility) => visibility switch
    {
        "private" => Loc.T("RepoPrivate"),
        "public" => Loc.T("RepoPublic"),
        "internal" => Loc.T("RepoInternal"),
        _ => visibility
    };

    private async Task LoadBranchesAsync()
    {
        var version = ++_branchVersion;
        _branchList.Items.Clear();
        _branches = new List<string>();
        UpdateButtons();
        if (Selected is not { } repository)
        {
            _branchesLabel.Text = Loc.T("RepoBranches");
            return;
        }
        _branchesLabel.Text = Loc.T("PrLoading");
        try
        {
            using var service = PullRequestServices.Create(_settings);
            var branches = await service.GetBranchesAsync(repository.Project, repository.Id);
            if (version != _branchVersion || IsDisposed) return;
            _branches = branches;
            // The default branch first, the rest in alphabetical order.
            foreach (var branch in branches.OrderBy(b => b != repository.DefaultBranch))
                _branchList.Items.Add(branch);
            _branchesLabel.Text = Loc.T("RepoBranchesOf", repository.Name, branches.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            if (version != _branchVersion || IsDisposed) return;
            // An empty Azure DevOps repository has no branches; its error is shown in place of the list.
            _branchesLabel.Text = Loc.T("RepoBranches");
            _branchList.Items.Add(ex.Message);
        }
        UpdateButtons();
    }

    // ---------- repositories ----------

    private async Task CreateRepositoryAsync()
    {
        // Azure DevOps: a team project from the list. GitHub: the user (empty) or one of the organizations seen.
        var owner = new ComboBox { Width = 320, DropDownStyle = _features.UsesProjects ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown };
        var owners = _features.UsesProjects ? _projects.ToList()
            : _repositories.Select(r => r.Project).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o).ToList();
        owner.Items.AddRange(owners.Cast<object>().ToArray());
        owner.Text = Selected?.Project ?? owners.FirstOrDefault() ?? "";
        if (_features.UsesProjects && owner.SelectedIndex < 0 && owner.Items.Count > 0) owner.SelectedIndex = 0;
        var name = new TextBox { Width = 320 };
        var fields = new List<(string, Control)>
        {
            (Loc.T(_features.UsesProjects ? "TaskProject" : "RepoColOwner"), owner), (Loc.T("RepoColName"), name)
        };
        // GitHub: public (anyone can see it) or private (only people given access); private is the safer start.
        var visibility = new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
        visibility.Items.AddRange(new object[] { Loc.T("RepoPrivateLong"), Loc.T("RepoPublicLong") });
        visibility.SelectedIndex = 0;
        if (_features.PrivateRepositories) fields.Add((Loc.T("RepoColVisibility"), visibility));

        if (!FieldsDialog.Show(this, Loc.T("RepoNew"), Loc.T("PrCreate"), Theme.Green, () => name.Text.Trim().Length > 0, fields.ToArray()))
            return;

        var isPrivate = visibility.SelectedIndex == 0;
        GitRepository? created = null;
        if (await RunAsync(async service => created = await service.CreateRepositoryAsync(owner.Text.Trim(), name.Text.Trim(), isPrivate)))
            await LoadAsync(created!.Id);
    }

    /// <summary>The name without the owner ("name" of "owner/name"), which is what GitHub renames.</summary>
    private static string ShortName(GitRepository repository) =>
        repository.Name.Contains('/') ? repository.Name[(repository.Name.LastIndexOf('/') + 1)..] : repository.Name;

    private async Task RenameRepositoryAsync()
    {
        if (Selected is not { } repository) return;
        var name = new TextBox { Width = 320, Text = ShortName(repository) };
        if (!FieldsDialog.Show(this, Loc.T("RepoRename"), Loc.T("RepoRename"), Theme.Primary,
                () => name.Text.Trim().Length > 0 && name.Text.Trim() != ShortName(repository), (Loc.T("RepoNewName"), name)))
            return;
        // A GitHub repository's ID is its "owner/name", so it changes with the name.
        var newId = _features.UsesProjects ? repository.Id : $"{repository.Project}/{name.Text.Trim()}";
        if (await RunAsync(service => service.RenameRepositoryAsync(repository, name.Text.Trim())))
            await LoadAsync(newId);
    }

    private async Task DeleteRepositoryAsync()
    {
        if (Selected is not { } repository) return;
        // Deleting is serious enough to ask for the name to be typed, as the websites do.
        var warning = new Label
        {
            AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Theme.Coral,
            Text = Loc.T(_features.UsesProjects ? "RepoDeleteWarnDevOps" : "RepoDeleteWarnGitHub", repository.Name)
        };
        var confirm = new TextBox { Width = 320, PlaceholderText = repository.Name };
        if (!FieldsDialog.Show(this, Loc.T("RepoDelete"), Loc.T("RepoDelete"), Theme.Coral,
                () => confirm.Text.Trim() == repository.Name, ("", warning), (Loc.T("RepoTypeName"), confirm)))
            return;

        // An account signed in before deleting was offered lacks GitHub's permission for it.
        string? Hint(Exception ex) => !_features.UsesProjects && ex.Message.StartsWith("403", StringComparison.Ordinal)
            ? Loc.T("RepoDeleteScope") : null;
        if (await RunAsync(service => service.DeleteRepositoryAsync(repository), Hint))
        {
            _settings.PullRequestRepositories.Remove(repository.Id);
            SettingsStore.Save(_settings);
            await LoadAsync();
        }
    }

    // ---------- branches ----------

    private async Task CreateBranchAsync()
    {
        if (Selected is not { } repository) return;
        var name = new TextBox { Width = 320 };
        var from = new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
        from.Items.AddRange(_branches.Cast<object>().ToArray());
        from.SelectedItem = _branchList.SelectedItem as string ?? repository.DefaultBranch;
        if (from.SelectedIndex < 0 && from.Items.Count > 0) from.SelectedIndex = 0;
        if (from.Items.Count == 0)
        {
            MessageBox.Show(this, Loc.T("BranchNoneToCopy"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, Loc.MsgOptions);
            return;
        }

        if (!FieldsDialog.Show(this, Loc.T("BranchNew"), Loc.T("PrCreate"), Theme.Green,
                () => name.Text.Trim().Length > 0 && !_branches.Contains(name.Text.Trim(), StringComparer.OrdinalIgnoreCase),
                (Loc.T("BranchName"), name), (Loc.T("BranchFrom"), from)))
            return;
        if (await RunAsync(service => service.CreateBranchAsync(repository, name.Text.Trim(), (string)from.SelectedItem!)))
        {
            await LoadBranchesAsync();
            _branchList.SelectedItem = name.Text.Trim();
        }
    }

    private async Task DeleteBranchAsync()
    {
        if (Selected is not { } repository || _branchList.SelectedItem is not string branch || branch == repository.DefaultBranch) return;
        var answer = MessageBox.Show(this, Loc.T("BranchDeleteConfirm", branch, repository.Name), Text, MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, Loc.MsgOptions);
        if (answer != DialogResult.Yes) return;
        if (await RunAsync(service => service.DeleteBranchAsync(repository, branch))) await LoadBranchesAsync();
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowError(ex);
        }
    }
}

/// <summary>Ticks the repositories the Pull requests window shows. Nothing ticked means all.</summary>
public sealed class RepositoryFilterForm : Form
{
    private readonly List<GitRepository> _repositories;
    private readonly HashSet<string> _chosen;
    private readonly bool _showProject;
    private readonly TextBox _search = new() { Width = 300, Margin = new Padding(3, 5, 3, 3) };
    private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly Label _count = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private bool _filling;

    /// <summary>The ticked repositories' IDs; empty = all.</summary>
    public IReadOnlyCollection<string> Chosen => _chosen;

    public RepositoryFilterForm(List<GitRepository> repositories, IEnumerable<string> chosen, bool showProject)
    {
        _repositories = repositories;
        _showProject = showProject;
        // IDs of repositories that no longer exist are dropped.
        _chosen = chosen.Where(id => repositories.Any(r => r.Id == id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Icon = Program.AppIcon;
        Text = Loc.T("PrRepoFilterTitle");
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(400, 300);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);

        var hint = new Label { Text = Loc.T(showProject ? "PrRepoFilterHint" : "PrRepoFilterHintGitHub"), AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 3, 3, 6) };
        _search.PlaceholderText = Loc.T("RepoSearch");
        var clear = new Button { Text = Loc.T("PrRepoFilterClear"), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(clear, Theme.Neutral);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 0), FlowDirection = FlowDirection.LeftToRight };
        top.Controls.AddRange(new Control[] { hint, _search, clear, _count });
        top.SetFlowBreak(hint, true);

        var ok = new Button { Text = Loc.T("OK"), AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(12, 3, 12, 3) };
        var cancel = new Button { Text = Loc.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(ok, Theme.Primary);
        Theme.StyleButton(cancel, Theme.Neutral);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 0) };
        body.Controls.Add(_list);

        Controls.Add(body);
        Controls.Add(top);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        _search.TextChanged += (_, _) => Fill();
        clear.Click += (_, _) => { _chosen.Clear(); Fill(); };
        _list.ItemCheck += (_, e) =>
        {
            if (_filling || _list.Items[e.Index] is not Option option) return;
            if (e.NewValue == CheckState.Checked) _chosen.Add(option.Repository.Id);
            else _chosen.Remove(option.Repository.Id);
            BeginInvoke(UpdateCount); // the tick itself is applied after this event
        };
        Fill();
    }

    private sealed record Option(GitRepository Repository, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>The ticked repositories first, then the others matching the search.</summary>
    private void Fill()
    {
        var text = _search.Text.Trim();
        var options = _repositories
            .Select(r => new Option(r, _showProject ? $"{r.Project} / {r.Name}" : r.Name))
            .Where(o => _chosen.Contains(o.Repository.Id) || text.Length == 0 || o.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => !_chosen.Contains(o.Repository.Id)).ThenBy(o => o.Text, StringComparer.OrdinalIgnoreCase);

        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var option in options) _list.Items.Add(option, _chosen.Contains(option.Repository.Id));
        _list.EndUpdate();
        _filling = false;
        UpdateCount();
    }

    private void UpdateCount() =>
        _count.Text = _chosen.Count == 0 ? Loc.T("PrRepoFilterNone") : Loc.T("PrLinksSelected", _chosen.Count);
}

/// <summary>A small dialog of labelled fields with OK and Cancel, used for the repository and branch actions.</summary>
internal static class FieldsDialog
{
    /// <param name="canAccept">Whether OK is enabled; checked again whenever a field changes.</param>
    /// <param name="fields">A label (may be empty) and its control, one row each.</param>
    public static bool Show(IWin32Window owner, string title, string okText, Color okColor, Func<bool> canAccept,
        params (string Label, Control Field)[] fields)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, Icon = Program.AppIcon,
            AutoScaleMode = AutoScaleMode.Font, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        Loc.ApplyDirection(form);
        Theme.StyleForm(form, Theme.Surface);

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12), Dock = DockStyle.Fill };
        foreach (var (label, field) in fields)
        {
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) });
            table.Controls.Add(field);
        }

        var ok = new Button { Text = okText, AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(12, 3, 12, 3) };
        var cancel = new Button { Text = Loc.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(ok, okColor);
        Theme.StyleButton(cancel, Theme.Neutral);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        form.Controls.Add(table);
        form.Controls.Add(buttons);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        void Check() => ok.Enabled = canAccept();
        foreach (var (_, field) in fields)
        {
            field.TextChanged += (_, _) => Check();
            if (field is ComboBox combo) combo.SelectedIndexChanged += (_, _) => Check();
        }
        Check();
        var accepted = form.ShowDialog(owner) == DialogResult.OK;
        // The fields belong to the caller, who reads them afterwards; take them out before the form is disposed.
        table.Controls.Clear();
        return accepted;
    }
}
