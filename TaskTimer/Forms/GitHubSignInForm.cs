using TaskTimer.Services;

namespace TaskTimer.Forms;

/// <summary>
/// Shows the code for the GitHub device sign-in, opens github.com in the browser and waits until the
/// user has signed in there and approved the code. Closes with OK and <see cref="Token"/> set.
/// </summary>
public sealed class GitHubSignInForm : Form
{
    private readonly string _apiUrl;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 3, 3, 12) };
    private readonly Label _code = new() { AutoSize = true, Font = new Font("Consolas", 24f, FontStyle.Bold), Margin = new Padding(3, 0, 3, 12) };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 0, 3, 12) };
    private readonly Button _open = new() { AutoSize = true, Padding = new Padding(12, 3, 12, 3), Enabled = false };
    private readonly Button _cancelButton = new() { AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 3, 8, 3) };
    private GitHubDeviceCode? _deviceCode;

    /// <summary>The access token, once the user has approved the sign-in.</summary>
    public string Token { get; private set; } = "";

    public GitHubSignInForm(string apiUrl)
    {
        _apiUrl = apiUrl;

        Icon = Program.AppIcon;
        Text = Loc.T("GitHubSignInTitle");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleButton(_open, Theme.Primary);
        Theme.StyleButton(_cancelButton, Theme.Neutral);
        _code.ForeColor = Theme.Primary;
        _status.ForeColor = Theme.Muted;

        _message.Text = Loc.T("GitHubSignInMsg");
        _code.Text = "····-····";
        _status.Text = Loc.T("GitHubGettingCode");
        _open.Text = Loc.T("GitHubOpen");
        _cancelButton.Text = Loc.T("Cancel");

        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.Add(_open);
        buttons.Controls.Add(_cancelButton);
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14), Dock = DockStyle.Fill
        };
        layout.Controls.AddRange(new Control[] { _message, _code, _status, buttons });
        Controls.Add(layout);
        CancelButton = _cancelButton;

        _open.Click += (_, _) => OpenGitHub();
        Shown += async (_, _) => await SignInAsync();
        FormClosed += (_, _) => _cancel.Cancel();
    }

    private async Task SignInAsync()
    {
        try
        {
            _deviceCode = await GitHubAuth.RequestCodeAsync(_apiUrl, _cancel.Token);
            _code.Text = _deviceCode.UserCode;
            _status.Text = Loc.T("GitHubWaiting");
            _open.Enabled = true;
            OpenGitHub();

            Token = await GitHubAuth.WaitForTokenAsync(_apiUrl, _deviceCode, _cancel.Token);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed.
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            if (IsDisposed) return;
            _open.Enabled = false;
            _status.ForeColor = Theme.Coral;
            _status.Text = ex.Message;
        }
    }

    /// <summary>Copies the code so it can be pasted on GitHub, and opens the page that asks for it.</summary>
    private void OpenGitHub()
    {
        if (_deviceCode == null) return;
        try { Clipboard.SetText(_deviceCode.UserCode); }
        catch (System.Runtime.InteropServices.ExternalException) { } // clipboard busy: the code is still on screen
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_deviceCode.VerificationUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = Loc.T("GitHubOpenManually", _deviceCode.VerificationUri);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancel.Dispose();
        base.Dispose(disposing);
    }
}
