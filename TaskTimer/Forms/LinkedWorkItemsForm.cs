using TaskTimer.Models;
using TaskTimer.Services;

namespace TaskTimer.Forms;

/// <summary>Lists the work items linked to a pull request; double-click opens one in the browser.</summary>
public sealed class LinkedWorkItemsForm : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };

    public static void Show(IWin32Window owner, AppSettings settings, PullRequest pr)
    {
        using var form = new LinkedWorkItemsForm(settings, pr);
        form.ShowDialog(owner);
    }

    private LinkedWorkItemsForm(AppSettings settings, PullRequest pr)
    {
        Icon = Program.AppIcon;
        Text = Loc.T("PrWorkItemsTitle", pr.Id);
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(820, 360);
        MinimumSize = new Size(500, 240);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleGrid(_grid);

        foreach (var (key, weight) in new[] { ("ColId", 30), ("ColType", 50), ("ColTitle", 220), ("ColState", 50), ("ColAssignedTo", 80) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Loc.T(key), FillWeight = weight });

        var open = new Button { Text = Loc.T("OpenInBrowser"), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        var close = new Button { Text = Loc.T("MachineClose"), AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(open, Theme.Primary);
        Theme.StyleButton(close, Theme.Neutral);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { open, close });
        Controls.Add(_grid);
        Controls.Add(buttons);
        CancelButton = close;

        open.Click += (_, _) => OpenSelected();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        Shown += async (_, _) => await LoadAsync(settings, pr);
    }

    private async Task LoadAsync(AppSettings settings, PullRequest pr)
    {
        UseWaitCursor = true;
        AddMessage(Loc.T("PrLoading"));
        try
        {
            using var service = PullRequestServices.Create(settings);
            var items = await service.GetLinkedWorkItemsAsync(pr);
            if (IsDisposed) return;
            _grid.Rows.Clear();
            foreach (var item in items)
            {
                var index = _grid.Rows.Add(item.Id, item.Type, item.Title, item.State, item.AssignedTo);
                _grid.Rows[index].Tag = item;
            }
            if (items.Count == 0) AddMessage(Loc.T("PrNoWorkItems"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            if (IsDisposed) return;
            _grid.Rows.Clear();
            AddMessage(ex.Message);
        }
        finally
        {
            if (!IsDisposed) UseWaitCursor = false;
        }
    }

    private void AddMessage(string text)
    {
        _grid.Rows.Clear();
        var index = _grid.Rows.Add("", "", text, "", "");
        _grid.Rows[index].DefaultCellStyle.ForeColor = Theme.Muted;
    }

    private void OpenSelected()
    {
        if (_grid.CurrentRow?.Tag is not LinkedWorkItem item) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.WebUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Loc.MsgOptions);
        }
    }
}
