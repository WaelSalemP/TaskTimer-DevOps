using TaskTimer.Models;
using TaskTimer.Services;

namespace TaskTimer.Forms;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly TextBox _accountName = new() { Dock = DockStyle.Fill };
    private readonly Button _deleteAccount = new() { AutoSize = true };
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

    /// <summary>Set when the user chose to remove this account; the main window does the rest.</summary>
    public bool DeleteRequested { get; private set; }
    private readonly Label _urlLabel = NewLabel();
    private readonly TextBox _url = new() { Dock = DockStyle.Fill };
    private readonly Label _projectLabel = NewLabel();
    private readonly TextBox _project = new() { Dock = DockStyle.Fill };
    private readonly Label _teamLabel = NewLabel();
    private readonly TextBox _team = new() { Dock = DockStyle.Fill };
    private readonly Label _customerFieldLabel = NewLabel();
    private readonly TextBox _customerField = new() { Dock = DockStyle.Fill, PlaceholderText = "System.AreaPath / Custom.Customer" };
    private readonly NumericUpDown _idleSeconds = new() { Maximum = 86400, Increment = 30, Width = 100, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown _minCallMinutes = new() { Maximum = 240, Width = 100, Anchor = AnchorStyles.Left };
    private readonly TextBox _workPaths = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { AutoSize = true, Dock = DockStyle.Right };
    private readonly Label _deliveryFieldLabel = NewLabel();
    private readonly TextBox _deliveryField = new() { Dock = DockStyle.Fill, PlaceholderText = "Delivery Date / Microsoft.VSTS.Scheduling.TargetDate" };
    private readonly Label _patLabel = NewLabel();
    private readonly TextBox _pat = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    // GitHub signs in through the browser instead of a pasted token.
    private readonly FlowLayoutPanel _gitHubPanel = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly Button _gitHubSignIn = new() { AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
    private readonly Label _gitHubStatus = new() { AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
    /// <summary>The GitHub access token from the browser sign-in, kept apart from an Azure DevOps token.</summary>
    private string _gitHubToken = "";
    private readonly CheckBox _autoDetect = new() { AutoSize = true };
    private readonly CheckBox _autoResume = new() { AutoSize = true };
    private readonly CheckBox _allowMultiple = new() { AutoSize = true };
    private readonly CheckBox _reduceRemaining = new() { AutoSize = true };
    private readonly CheckBox _addComment = new() { AutoSize = true };
    private readonly Button _test = new() { AutoSize = true };
    private readonly Button _save = new() { AutoSize = true };
    private readonly Button _cancel = new() { AutoSize = true, DialogResult = DialogResult.Cancel };

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        Icon = Program.AppIcon;
        Text = Loc.T("Settings");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(720, 655);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleButton(_save, Theme.Primary);
        Theme.StyleButton(_test, Theme.Neutral);
        Theme.StyleButton(_cancel, Theme.Neutral);
        foreach (var button in new[] { _save, _test, _cancel }) button.Padding = new Padding(8, 3, 8, 3);

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _provider.Items.AddRange(new object[] { "Azure DevOps", "GitHub" });
        _autoDetect.Text = Loc.T("AutoDetect");
        _autoResume.Text = Loc.T("AutoResume");
        _allowMultiple.Text = Loc.T("AllowMultiple");
        _reduceRemaining.Text = Loc.T("ReduceRemaining");
        _addComment.Text = Loc.T("AddComment");
        _test.Text = Loc.T("Test");
        _save.Text = Loc.T("Save");
        _cancel.Text = Loc.T("Cancel");

        AddRow(table, NewLabel(Loc.T("AccountName")), _accountName);
        AddRow(table, NewLabel(Loc.T("Provider")), _provider);
        AddRow(table, _urlLabel, _url);
        AddRow(table, _projectLabel, _project);
        _teamLabel.Text = Loc.T("Team");
        AddRow(table, _teamLabel, _team);
        _customerFieldLabel.Text = Loc.T("CustomerField");
        AddRow(table, _customerFieldLabel, _customerField);
        _deliveryFieldLabel.Text = Loc.T("DeliveryField");
        AddRow(table, _deliveryFieldLabel, _deliveryField);
        AddRow(table, NewLabel(Loc.T("IdleSeconds")), _idleSeconds);
        AddRow(table, NewLabel(Loc.T("MinCallMinutes")), _minCallMinutes);

        _browse.Text = Loc.T("Browse");
        var workPathsPanel = new Panel { Dock = DockStyle.Fill, Height = _browse.PreferredSize.Height + 2, Margin = new Padding(3) };
        workPathsPanel.Controls.Add(_workPaths);
        workPathsPanel.Controls.Add(_browse);
        AddRow(table, NewLabel(Loc.T("WorkPaths")), workPathsPanel);
        _browse.Click += (_, _) => BrowseWorkPath();
        Theme.StyleButton(_gitHubSignIn, Theme.Primary);
        _gitHubStatus.ForeColor = Theme.Muted;
        _gitHubPanel.Controls.AddRange(new Control[] { _gitHubSignIn, _gitHubStatus });
        _gitHubSignIn.Click += async (_, _) => await SignInToGitHubAsync();
        var credentialPanel = new Panel { Dock = DockStyle.Fill, Height = _gitHubSignIn.PreferredSize.Height + 8, Margin = new Padding(3) };
        credentialPanel.Controls.Add(_pat);
        credentialPanel.Controls.Add(_gitHubPanel);
        AddRow(table, _patLabel, credentialPanel);
        foreach (var check in new[] { _autoDetect, _autoResume, _allowMultiple, _reduceRemaining, _addComment })
        {
            table.Controls.Add(check);
            table.SetColumnSpan(check, 2);
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            Padding = new Padding(8)
        };
        // Removing an account only makes sense while another one is left.
        _deleteAccount.Text = Loc.T("DeleteAccount");
        _deleteAccount.Padding = new Padding(8, 3, 8, 3);
        _deleteAccount.Visible = settings.Connections.Count > 1;
        Theme.StyleButton(_deleteAccount, Theme.Coral);
        _deleteAccount.Click += (_, _) =>
        {
            var answer = MessageBox.Show(this, Loc.T("ConfirmDeleteAccount", settings.ConnectionName), Text, MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, Loc.MsgOptions);
            if (answer != DialogResult.Yes) return;
            DeleteRequested = true;
            DialogResult = DialogResult.Abort;
            Close();
        };
        buttons.Controls.AddRange(new Control[] { _cancel, _save, _test, _deleteAccount });

        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = _save;
        CancelButton = _cancel;

        _accountName.Text = settings.ConnectionName;
        _provider.SelectedIndex = (int)settings.Provider;
        _url.Text = settings.Url;
        _project.Text = settings.Project;
        _team.Text = settings.Team;
        _customerField.Text = settings.CustomerField;
        _deliveryField.Text = settings.DeliveryDateField;
        _workPaths.Text = settings.WorkPaths;
        _idleSeconds.Value = Math.Clamp(settings.IdlePauseSeconds, 0, (int)_idleSeconds.Maximum);
        _minCallMinutes.Value = Math.Clamp(settings.MinCallMinutes, 0, (int)_minCallMinutes.Maximum);
        if (settings.Provider == ProviderKind.GitHub) _gitHubToken = SettingsStore.GetPat(settings);
        else _pat.Text = SettingsStore.GetPat(settings);
        _autoDetect.Checked = settings.AutoDetectTeams;
        _autoResume.Checked = settings.AutoResumeLastTask;
        _allowMultiple.Checked = settings.AllowMultipleTasks;
        _reduceRemaining.Checked = settings.ReduceRemainingWork;
        _addComment.Checked = settings.AddComment;

        _provider.SelectedIndexChanged += (_, _) => UpdateProviderFields();
        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => SaveAndClose();
        UpdateProviderFields();
    }

    private static Label NewLabel(string text = "") =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) };

    private static void AddRow(TableLayoutPanel table, Control label, Control field)
    {
        table.Controls.Add(label);
        table.Controls.Add(field);
    }

    private void BrowseWorkPath()
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var current = _workPaths.Text.Trim().TrimEnd(';');
        _workPaths.Text = current.Length == 0 ? dialog.SelectedPath : current + ";" + dialog.SelectedPath;
    }

    private bool IsGitHub => _provider.SelectedIndex == (int)ProviderKind.GitHub;

    private void UpdateProviderFields()
    {
        _urlLabel.Text = Loc.T(IsGitHub ? "ApiUrl" : "OrgUrl");
        _url.PlaceholderText = IsGitHub ? "https://api.github.com" : "https://dev.azure.com/your-organization";
        _projectLabel.Enabled = _project.Enabled = !IsGitHub;
        _customerFieldLabel.Enabled = _customerField.Enabled = !IsGitHub;
        _deliveryFieldLabel.Enabled = _deliveryField.Enabled = !IsGitHub;
        _teamLabel.Enabled = _team.Enabled = !IsGitHub;
        _reduceRemaining.Enabled = _addComment.Enabled = !IsGitHub;
        _patLabel.Text = Loc.T(IsGitHub ? "GitHubAccount" : "Pat");
        _pat.Visible = !IsGitHub;
        _gitHubPanel.Visible = IsGitHub;
        UpdateGitHubStatus();
    }

    private void UpdateGitHubStatus(string login = "")
    {
        _gitHubSignIn.Text = Loc.T(_gitHubToken.Length == 0 ? "GitHubSignIn" : "GitHubSignInAgain");
        _gitHubStatus.Text = _gitHubToken.Length == 0 ? Loc.T("GitHubNotSignedIn")
            : login.Length > 0 ? Loc.T("GitHubSignedInAs", login)
            : Loc.T("GitHubSignedIn");
    }

    private async Task SignInToGitHubAsync()
    {
        using var form = new GitHubSignInForm(_url.Text);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _gitHubToken = form.Token;
        UpdateGitHubStatus();
        try
        {
            UpdateGitHubStatus(await GitHubAuth.GetLoginAsync(_url.Text, _gitHubToken));
        }
        catch (HttpRequestException)
        {
            // Signed in all the same; the name is only for display.
        }
    }

    private string Credential => IsGitHub ? _gitHubToken : _pat.Text.Trim();

    private bool TryRead(AppSettings target)
    {
        if (Credential.Length == 0 || (!IsGitHub && _url.Text.Trim().Length == 0))
        {
            MessageBox.Show(this, Loc.T(IsGitHub ? "GitHubRequired" : "Required"), Text, MessageBoxButtons.OK,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1, Loc.MsgOptions);
            return false;
        }

        target.Provider = (ProviderKind)_provider.SelectedIndex;
        target.ConnectionName = _accountName.Text.Trim();
        target.Url = _url.Text.Trim();
        target.Project = _project.Text.Trim();
        target.Team = _team.Text.Trim();
        target.CustomerField = _customerField.Text.Trim();
        target.DeliveryDateField = _deliveryField.Text.Trim();
        target.WorkPaths = _workPaths.Text.Trim();
        target.IdlePauseSeconds = (int)_idleSeconds.Value;
        target.MinCallMinutes = (int)_minCallMinutes.Value;
        SettingsStore.SetPat(target, Credential);
        target.AutoDetectTeams = _autoDetect.Checked;
        target.AutoResumeLastTask = _autoResume.Checked;
        target.AllowMultipleTasks = _allowMultiple.Checked;
        target.ReduceRemainingWork = _reduceRemaining.Checked;
        target.AddComment = _addComment.Checked;
        return true;
    }

    private async Task TestAsync()
    {
        var candidate = new AppSettings();
        if (!TryRead(candidate)) return;

        _test.Enabled = false;
        UseWaitCursor = true;
        try
        {
            using var provider = ProviderFactory.Create(candidate);
            await provider.TestAsync();
            MessageBox.Show(this, Loc.T("TestOk"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, Loc.MsgOptions);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, Loc.MsgOptions);
        }
        finally
        {
            UseWaitCursor = false;
            _test.Enabled = true;
        }
    }

    private void SaveAndClose()
    {
        if (!TryRead(_settings)) return;
        SettingsStore.Save(_settings);
        DialogResult = DialogResult.OK;
        Close();
    }
}
