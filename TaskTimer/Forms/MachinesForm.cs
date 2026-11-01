using System.ComponentModel;
using TaskTimer.Models;
using TaskTimer.Services;
using PowerState = TaskTimer.Services.PowerState;

namespace TaskTimer.Forms;

/// <summary>Manages the list of dev machines (on-premises or cloud) and opens remote connections to them.</summary>
public sealed class MachinesForm : Form
{
    private readonly AppSettings _settings;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };

    public MachinesForm(AppSettings settings)
    {
        _settings = settings;

        Icon = Program.AppIcon;
        Text = Loc.T("MachinesTitle");
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 420);
        MinimumSize = new Size(560, 300);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleGrid(_grid);

        foreach (var key in new[] { "MachineName", "MachineLocation", "MachineAddress", "MachineUser", "PasswordSaved", "MachineStatusCol" })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Loc.T(key), SortMode = DataGridViewColumnSortMode.NotSortable });

        var add = NewButton("Add", Theme.Green);
        var edit = NewButton("MachineEdit", Theme.Neutral);
        var remove = NewButton("Remove", Theme.Neutral);
        var connect = _connect = NewButton("Connect", Theme.Primary);
        var close = NewButton("MachineClose", Theme.Neutral);
        var refresh = NewButton("RefreshStatus", Theme.Neutral);
        _start = NewButton("StartMachine", Theme.Green);
        _stop = NewButton("StopMachine", Theme.Coral);
        refresh.Click += async (_, _) => await RefreshStatusesAsync(interactive: true);
        _start.Click += async (_, _) => await ChangePowerFromListAsync(start: true);
        _stop.Click += async (_, _) => await ChangePowerFromListAsync(start: false);
        _grid.SelectionChanged += (_, _) => UpdatePowerButtons();
        close.DialogResult = DialogResult.OK;
        add.Click += (_, _) => Edit(null);
        edit.Click += (_, _) => { if (Selected is { } m) Edit(m); };
        remove.Click += (_, _) => Remove();
        connect.Click += (_, _) => { if (Selected is { } m) Connect(this, m); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && Selected is { } m && _connect.Enabled) Connect(this, m); };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { add, edit, remove, connect, refresh, _start, _stop, close });

        Controls.Add(_grid);
        Controls.Add(buttons);
        CancelButton = close;
        Reload();
    }

    private static Button NewButton(string key, Color color)
    {
        var button = new Button { Text = Loc.T(key), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(button, color);
        return button;
    }

    private readonly Button _start;
    private readonly Button _stop;
    private const int StatusColumn = 5;

    private DevMachine? Selected => _grid.CurrentRow?.Tag as DevMachine;

    private void UpdatePowerButtons()
    {
        var machine = Selected;
        var state = machine != null && _states.TryGetValue(machine.Id, out var known) ? known : null;
        (_start.Enabled, _stop.Enabled, _connect.Enabled) = PowerButtons(machine, state);
        _stop.Text = Loc.T(machine?.Location == MachineLocation.OnPremises ? "SaveMachine" : "StopMachine");
    }

    /// <summary>Last known power state by machine ID; missing or null while it is not known.</summary>
    private readonly Dictionary<string, PowerState?> _states = new();
    private readonly Button _connect;

    private DataGridViewRow? RowOf(DevMachine machine) =>
        _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as DevMachine)?.Id == machine.Id);

    private void ShowStatus(DevMachine machine, string text)
    {
        if (IsDisposed || RowOf(machine) is not { } row) return;
        row.Cells[StatusColumn].Value = text;
    }

    /// <summary>Asks every managed machine for its state; the rows fill in as the answers arrive.</summary>
    /// <param name="interactive">True when the user pressed Refresh: a Microsoft sign-in may be shown.</param>
    private async Task RefreshStatusesAsync(bool interactive = false)
    {
        var machines = _grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<DevMachine>().ToList();
        foreach (var machine in machines)
            ShowStatus(machine, Loc.T(MachinePower.IsManaged(machine) ? "PowerChecking" : "PowerNotManaged"));
        await Task.WhenAll(machines.Where(MachinePower.IsManaged).Select(RefreshStatusAsync));

        async Task RefreshStatusAsync(DevMachine machine)
        {
            var (state, text) = await StatusAsync(machine, interactive);
            if (IsDisposed) return;
            _states[machine.Id] = state;
            ShowStatus(machine, text);
            if (Selected?.Id == machine.Id) UpdatePowerButtons();
        }
    }

    /// <summary>
    /// The power state and its text for display. A failure is shown as "Error" with the first line of its
    /// message; the state is then null, as it is when the server reports a state the app does not know.
    /// </summary>
    public static async Task<(PowerState? State, string Text)> StatusAsync(DevMachine machine, bool interactive = false,
        string signInKey = "PowerSignInList")
    {
        try
        {
            var status = await MachinePower.GetStatusAsync(machine, interactive);
            var text = status.Key != null ? Loc.T(status.Key) : status.Raw.Length > 0 ? status.Raw : Loc.T("PowerUnknown");
            return (status.State == PowerState.Unknown ? null : status.State, text);
        }
        catch (SignInRequiredException)
        {
            return (null, Loc.T(signInKey));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (null, Loc.T("PowerError") + ": " + ex.Message.Split('\n')[0].Trim());
        }
    }

    /// <summary>
    /// Which of Start, Stop/Save and Connect make sense: a stopped or saved machine can only be started,
    /// a running one stopped or connected to, and one that is starting or stopping waits. While the
    /// state is not known (still checking, sign-in needed, error), everything stays available.
    /// </summary>
    public static (bool Start, bool Stop, bool Connect) PowerButtons(DevMachine? machine, PowerState? state)
    {
        if (machine == null) return (false, false, false);
        // Without VM details the app cannot start or stop it, nor tell whether it is on.
        if (!MachinePower.IsManaged(machine)) return (false, false, true);
        return state switch
        {
            PowerState.Running => (false, true, true),
            PowerState.Stopped => (true, false, false),
            PowerState.Busy => (false, false, false),
            _ => (true, true, true)
        };
    }

    private async Task ChangePowerFromListAsync(bool start)
    {
        if (Selected is not { } machine) return;
        _start.Enabled = _stop.Enabled = _connect.Enabled = false;
        ShowStatus(machine, Loc.T(start ? "PowerStarting" : machine.Location == MachineLocation.Cloud ? "PowerStopping" : "PowerSaving"));
        await ChangePowerAsync(this, machine, start);
        var (state, text) = await StatusAsync(machine, interactive: true);
        if (IsDisposed) return;
        _states[machine.Id] = state;
        ShowStatus(machine, text);
        UpdatePowerButtons();
    }

    /// <summary>Starts, or stops/saves, a machine; asks first before deallocating a cloud VM. Reports errors itself.</summary>
    public static async Task<bool> ChangePowerAsync(IWin32Window owner, DevMachine machine, bool start)
    {
        if (!start && machine.Location == MachineLocation.Cloud)
        {
            var answer = MessageBox.Show(owner, Loc.T("ConfirmStopMachine", machine.Name), Loc.T("MachinesTitle"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, Loc.MsgOptions);
            if (answer != DialogResult.Yes) return false;
        }
        try
        {
            if (start) await MachinePower.StartAsync(machine);
            else await MachinePower.StopAsync(machine);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(owner, ex.Message, Loc.T("MachinesTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, Loc.MsgOptions);
            return false;
        }
    }

    private void Reload(DevMachine? select = null)
    {
        _grid.Rows.Clear();
        _states.Clear(); // details may have changed; every status is read again below
        foreach (var machine in _settings.DevMachines.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var index = _grid.Rows.Add(machine.Name, Loc.T(machine.Location == MachineLocation.Cloud ? "Cloud" : "OnPremises"),
                machine.Address, machine.UserName, Loc.T(RemoteDesktop.HasPassword(machine) ? "MachineYes" : "MachineNo"),
                Loc.T(MachinePower.IsManaged(machine) ? "PowerChecking" : "PowerNotManaged"));
            _grid.Rows[index].Tag = machine;
            if (machine.Id == select?.Id) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }
        UpdatePowerButtons();
        _ = RefreshStatusesAsync();
    }

    private void Edit(DevMachine? existing)
    {
        using var form = new MachineEditForm(existing);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        var machine = form.Machine;

        try
        {
            // Credential Manager entries are keyed by address and hold the user name, so on any
            // change the old entry is removed and the password written again under the new details.
            var password = form.Password ?? (existing != null ? RemoteDesktop.ReadPassword(existing) : null);
            if (existing != null) RemoteDesktop.DeletePassword(existing);
            if (password != null && machine.UserName.Length > 0 && !RemoteDesktop.IsWebAddress(machine.Address))
                RemoteDesktop.SavePassword(machine, password);
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, Loc.MsgOptions);
        }

        _settings.DevMachines.RemoveAll(m => m.Id == machine.Id);
        _settings.DevMachines.Add(machine);
        SettingsStore.Save(_settings);
        Reload(machine);
    }

    private void Remove()
    {
        if (Selected is not { } machine) return;
        var answer = MessageBox.Show(this, Loc.T("ConfirmRemoveMachine", machine.Name), Text, MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, Loc.MsgOptions);
        if (answer != DialogResult.Yes) return;

        RemoteDesktop.DeletePassword(machine);
        _settings.DevMachines.RemoveAll(m => m.Id == machine.Id);
        SettingsStore.Save(_settings);
        Reload();
    }

    public static void Connect(IWin32Window owner, DevMachine machine)
    {
        try
        {
            RemoteDesktop.Connect(machine);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, ex.Message, Loc.T("MachinesTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, Loc.MsgOptions);
        }
    }
}
