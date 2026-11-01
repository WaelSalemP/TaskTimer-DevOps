using TaskTimer.Models;
using TaskTimer.Services;

namespace TaskTimer.Forms;

/// <summary>Lists Dynamics 365 tickets and lets the user create, edit, resolve, cancel, reopen and time them.</summary>
public sealed class TicketsForm : Form
{
    private readonly AppSettings _settings;
    private readonly Action<Ticket> _startTimer;
    private List<Ticket> _tickets = new();

    private readonly ComboBox _state = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(3, 5, 3, 3) };
    private readonly CheckBox _onlyMine = new() { AutoSize = true, Checked = true, Margin = new Padding(12, 8, 12, 3) };
    private readonly TextBox _search = new() { Width = 240, Margin = new Padding(3, 5, 3, 3) };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private readonly Button _refresh = NewButton("PrRefresh", Theme.Neutral);
    private readonly Button _signIn = NewButton("CrmConnection", Theme.Neutral);
    private readonly Button _new = NewButton("TicketNew", Theme.Green);
    private readonly Button _edit = NewButton("TicketEdit", Theme.Neutral);
    private readonly Button _notes = NewButton("TicketNotes", Theme.Neutral);
    private readonly Button _timer = NewButton("TicketStartTimer", Theme.Primary);
    private readonly Button _resolve = NewButton("TicketResolve", Theme.Violet);
    private readonly Button _cancelTicket = NewButton("TicketCancel", Theme.Coral);
    private readonly Button _reopen = NewButton("TicketReopen", Theme.Neutral);
    private readonly Button _open = NewButton("OpenInBrowser", Theme.Neutral);

    /// <param name="startTimer">Puts the ticket in the task list and starts timing it.</param>
    public TicketsForm(AppSettings settings, Action<Ticket> startTimer)
    {
        _settings = settings;
        _startTimer = startTimer;

        Icon = Program.AppIcon;
        Text = Loc.T("TicketsTitle");
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1150, 560);
        MinimumSize = new Size(760, 380);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleGrid(_grid);

        foreach (var (key, weight) in new[] { ("TicketColNumber", 70), ("TicketColTitle", 220), ("TicketColCustomer", 100), ("TicketColOwner", 90),
                     ("TicketColPriority", 50), ("TicketColStatus", 70), ("PrColCreated", 60) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Loc.T(key), FillWeight = weight });

        _state.Items.AddRange(new object[] { Loc.T("TicketStateActive"), Loc.T("TicketStateResolved"), Loc.T("TicketStateCancelled"), Loc.T("TicketStateAll") });
        _state.SelectedIndex = 0;
        _onlyMine.Text = Loc.T("TicketOnlyMine");
        _search.PlaceholderText = Loc.T("TicketSearch");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 0) };
        top.Controls.AddRange(new Control[] { _state, _onlyMine, _search, _refresh, _signIn, _status });

        var close = NewButton("MachineClose", Theme.Neutral);
        close.DialogResult = DialogResult.OK;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { _new, _edit, _notes, _timer, _resolve, _cancelTicket, _reopen, _open, close });

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);
        CancelButton = close;

        _state.SelectedIndexChanged += async (_, _) => await LoadAsync();
        _onlyMine.CheckedChanged += async (_, _) => await LoadAsync();
        _search.TextChanged += (_, _) => ShowList();
        _refresh.Click += async (_, _) => await LoadAsync();
        _signIn.Click += async (_, _) => { if (ShowSignIn()) await LoadAsync(); };
        _new.Click += async (_, _) => await CreateAsync();
        _edit.Click += async (_, _) => await EditAsync();
        _notes.Click += (_, _) => { if (Selected is { } t) { using var form = new TicketNotesForm(_settings, t); form.ShowDialog(this); } };
        _timer.Click += (_, _) => { if (Selected is { } t) { _startTimer(t); _status.Text = Loc.T("TicketTiming", t.Number); } };
        _resolve.Click += async (_, _) => await ResolveAsync();
        _cancelTicket.Click += async (_, _) => await CancelTicketAsync();
        _reopen.Click += async (_, _) => { if (Selected is { } t && await RunAsync(s => s.ReopenAsync(t))) await LoadAsync(); };
        _open.Click += (_, _) => { if (Selected is { } t) OpenInBrowser(t.WebUrl); };
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await EditAsync(); };
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        Shown += async (_, _) => await LoadAsync();
        UpdateButtons();
    }

    private static Button NewButton(string key, Color color)
    {
        var button = new Button { Text = Loc.T(key), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(button, color);
        return button;
    }

    private Ticket? Selected => _grid.CurrentRow?.Tag as Ticket;

    private void UpdateButtons()
    {
        var ticket = Selected;
        var active = ticket is { State: CrmService.Active };
        _edit.Enabled = _timer.Enabled = _resolve.Enabled = _cancelTicket.Enabled = active;
        _reopen.Enabled = ticket != null && !active;
        _notes.Enabled = _open.Enabled = ticket != null;
    }

    private bool ShowSignIn()
    {
        using var form = new CrmSignInForm(_settings);
        return form.ShowDialog(this) == DialogResult.OK;
    }

    /// <summary>Runs a call with a connection, asking for a sign-in first when one is needed. Reports errors itself.</summary>
    private async Task<bool> RunAsync(Func<CrmService, Task> action)
    {
        UseWaitCursor = true;
        try
        {
            CrmService service;
            try { service = await CrmService.ConnectAsync(_settings); }
            catch (SignInRequiredException)
            {
                UseWaitCursor = false;
                if (!ShowSignIn()) return false;
                UseWaitCursor = true;
                service = await CrmService.ConnectAsync(_settings);
            }
            using (service) await action(service);
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
            if (!IsDisposed) UseWaitCursor = false;
        }
    }

    private async Task LoadAsync(string? select = null)
    {
        select ??= Selected?.Id;
        _status.Text = Loc.T("PrLoading");
        int? state = _state.SelectedIndex is >= 0 and <= 2 ? _state.SelectedIndex : null;
        var mine = _onlyMine.Checked;
        var loaded = await RunAsync(async s => _tickets = await s.GetTicketsAsync(state, mine));
        if (IsDisposed) return;
        if (!loaded) _tickets = new List<Ticket>();
        ShowList(select);
    }

    private void ShowList(string? select = null)
    {
        select ??= Selected?.Id;
        var text = _search.Text.Trim();
        var shown = _tickets.Where(t => text.Length == 0 || t.Number.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                        t.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                        t.Customer.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _grid.Rows.Clear();
        foreach (var ticket in shown)
        {
            var index = _grid.Rows.Add(ticket.Number, ticket.Title, ticket.Customer, ticket.Owner?.Name ?? "", ticket.PriorityText, ticket.Status,
                ticket.Created?.ToString("yyyy-MM-dd") ?? "");
            _grid.Rows[index].Tag = ticket;
            if (ticket.Id == select) _grid.CurrentCell = _grid.Rows[index].Cells[0];
        }
        _status.Text = Loc.T("TicketCount", shown.Count);
        UpdateButtons();
    }

    private static ComboBox PriorityBox(int priority)
    {
        var box = new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
        box.Items.AddRange(new object[] { Loc.T("TicketPriorityHigh"), Loc.T("TicketPriorityNormal"), Loc.T("TicketPriorityLow") });
        box.SelectedIndex = Math.Clamp(priority - 1, 0, 2);
        return box;
    }

    private async Task CreateAsync()
    {
        var title = new TextBox { Width = 420 };
        var customer = new LookupBox(SearchAsync((s, text) => s.SearchCustomersAsync(text)));
        var priority = PriorityBox(2);
        var description = new TextBox { Width = 420, Height = 110, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        // The customer is required by Dynamics 365; it is chosen after a search, so OK re-checks on selection too.
        if (!FieldsDialog.Show(this, Loc.T("TicketNew"), Loc.T("PrCreate"), Theme.Green, () => title.Text.Trim().Length > 0 && customer.Selected != null,
                (Loc.T("TicketColTitle"), title), (Loc.T("TicketColCustomer"), customer), (Loc.T("TicketColPriority"), priority), (Loc.T("TaskDescription"), description)))
            return;
        Ticket? created = null;
        if (await RunAsync(async s => created = await s.CreateTicketAsync(title.Text.Trim(), description.Text.Trim(), customer.Selected!, priority.SelectedIndex + 1)))
            await LoadAsync(created!.Id);
    }

    private async Task EditAsync()
    {
        if (Selected is not { State: CrmService.Active } ticket) return;
        var title = new TextBox { Width = 420, Text = ticket.Title };
        var priority = PriorityBox(ticket.Priority);
        var owner = new LookupBox(SearchAsync((s, text) => s.SearchUsersAsync(text)), ticket.Owner);
        var description = new TextBox { Width = 420, Height = 110, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Text = ticket.Description };
        if (!FieldsDialog.Show(this, Loc.T("TicketEdit"), Loc.T("Save"), Theme.Primary, () => title.Text.Trim().Length > 0,
                (Loc.T("TicketColTitle"), title), (Loc.T("TicketColOwner"), owner), (Loc.T("TicketColPriority"), priority), (Loc.T("TaskDescription"), description)))
            return;
        var newOwner = owner.Selected != null && owner.Selected.Id != ticket.Owner?.Id ? owner.Selected : null;
        if (await RunAsync(s => s.UpdateTicketAsync(ticket, title.Text.Trim(), description.Text.Trim(), priority.SelectedIndex + 1, newOwner)))
            await LoadAsync(ticket.Id);
    }

    private Func<string, Task<List<CrmRef>>> SearchAsync(Func<CrmService, string, Task<List<CrmRef>>> search) => async text =>
    {
        var found = new List<CrmRef>();
        await RunAsync(async s => found = await search(s, text));
        return found;
    };

    private async Task ResolveAsync()
    {
        if (Selected is not { } ticket) return;
        var resolution = CommentPrompt.Ask(this, Loc.T("TicketResolution", ticket.Number));
        if (string.IsNullOrWhiteSpace(resolution)) return;
        if (await RunAsync(s => s.ResolveAsync(ticket, resolution.Trim()))) await LoadAsync();
    }

    private async Task CancelTicketAsync()
    {
        if (Selected is not { } ticket) return;
        var answer = MessageBox.Show(this, Loc.T("TicketCancelConfirm", ticket.Number, ticket.Title), Text, MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, Loc.MsgOptions);
        if (answer == DialogResult.Yes && await RunAsync(s => s.CancelAsync(ticket))) await LoadAsync();
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

/// <summary>A search box with a list of results to choose a customer or user from.</summary>
internal sealed class LookupBox : FlowLayoutPanel
{
    private readonly Func<string, Task<List<CrmRef>>> _search;
    private readonly TextBox _text = new() { Width = 220, PlaceholderText = Loc.T("TicketLookupHint") };
    private readonly ComboBox _results = new() { Width = 420, DropDownStyle = ComboBoxStyle.DropDownList };

    public CrmRef? Selected => _results.SelectedItem as CrmRef;

    public LookupBox(Func<string, Task<List<CrmRef>>> search, CrmRef? initial = null)
    {
        _search = search;
        AutoSize = true;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = new Padding(0);
        var find = new Button { Text = Loc.T("TicketLookup"), AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        Theme.StyleButton(find, Theme.Neutral);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(new Control[] { _text, find });
        Controls.AddRange(new Control[] { row, _results });

        if (initial != null)
        {
            _results.Items.Add(initial);
            _results.SelectedIndex = 0;
        }
        find.Click += async (_, _) => await FindAsync();
        _text.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; // Enter searches instead of pressing the dialog's OK button
            await FindAsync();
        };
        _results.SelectedIndexChanged += (_, _) => OnTextChanged(EventArgs.Empty); // lets the dialog re-check OK
    }

    private async Task FindAsync()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0) return;
        var found = await _search(text);
        if (IsDisposed) return;
        _results.Items.Clear();
        _results.Items.AddRange(found.Cast<object>().ToArray());
        if (found.Count > 0) _results.SelectedIndex = 0;
    }
}

/// <summary>Asks for the CRM address, user name and password, and signs in.</summary>
public sealed class CrmSignInForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _url = new() { Width = 380, PlaceholderText = "https://yourorg.crm4.dynamics.com" };
    private readonly TextBox _user = new() { Width = 380, PlaceholderText = "name@company.com" };
    private readonly TextBox _password = new() { Width = 380, UseSystemPasswordChar = true };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly Button _ok = new() { Text = Loc.T("CrmSignIn"), AutoSize = true, Padding = new Padding(12, 3, 12, 3) };
    private readonly Button _browser = new() { Text = Loc.T("CrmSignInBrowser"), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };

    public CrmSignInForm(AppSettings settings)
    {
        _settings = settings;
        Icon = Program.AppIcon;
        Text = Loc.T("CrmConnection");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        _url.Text = settings.CrmUrl;
        _user.Text = settings.CrmUserName;
        _message.Text = Loc.T("CrmPasswordHint");
        _message.ForeColor = Theme.Muted;

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12), Dock = DockStyle.Fill };
        foreach (var (key, field) in new (string, Control)[] { ("CrmUrl", _url), ("CrmUser", _user), ("CrmPassword", _password) })
        {
            table.Controls.Add(new Label { Text = Loc.T(key), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) });
            table.Controls.Add(field);
        }
        table.Controls.Add(new Label());
        table.Controls.Add(_message);

        var cancel = new Button { Text = Loc.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(_ok, Theme.Primary);
        Theme.StyleButton(_browser, Theme.Neutral);
        Theme.StyleButton(cancel, Theme.Neutral);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { cancel, _ok, _browser });
        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;

        _ok.Click += async (_, _) => await SignInAsync(_password.Text);
        _browser.Click += async (_, _) => await SignInAsync(null);
    }

    /// <param name="password">Null signs in through the browser.</param>
    private async Task SignInAsync(string? password)
    {
        var url = CrmService.NormalizeUrl(_url.Text);
        var user = _user.Text.Trim();
        var problem = url == null ? "CrmBadUrl" : user.Length == 0 ? "CrmNeedUser" : password is { Length: 0 } ? "CrmNeedPassword" : null;
        if (problem != null)
        {
            Show(Loc.T(problem));
            return;
        }

        _ok.Enabled = _browser.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await CrmAuth.SignInAsync(url!, user, password);
            _settings.CrmUrl = url!;
            _settings.CrmUserName = user;
            SettingsStore.Save(_settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            if (!IsDisposed) Show(ex.Message);
        }
        finally
        {
            _password.Clear();
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                _ok.Enabled = _browser.Enabled = true;
            }
        }

        void Show(string text)
        {
            _message.ForeColor = Theme.Coral;
            _message.Text = text;
        }
    }
}

/// <summary>Shows a ticket's notes, newest first, and adds new ones.</summary>
public sealed class TicketNotesForm : Form
{
    private readonly AppSettings _settings;
    private readonly Ticket _ticket;
    private readonly TextBox _notes = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

    public TicketNotesForm(AppSettings settings, Ticket ticket)
    {
        _settings = settings;
        _ticket = ticket;
        Icon = Program.AppIcon;
        Text = Loc.T("TicketNotesTitle", ticket.Number);
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(640, 440);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);

        var add = new Button { Text = Loc.T("TicketAddNote"), AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
        var close = new Button { Text = Loc.T("MachineClose"), AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(add, Theme.Primary);
        Theme.StyleButton(close, Theme.Neutral);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { add, close });
        Controls.Add(_notes);
        Controls.Add(buttons);
        CancelButton = close;

        add.Click += async (_, _) =>
        {
            var text = CommentPrompt.Ask(this, Loc.T("TicketAddNote"));
            if (string.IsNullOrWhiteSpace(text)) return;
            await RunAsync(async s =>
            {
                await s.AddNoteAsync(_ticket.Id, text.Trim());
                await ShowNotesAsync(s);
            });
        };
        Shown += async (_, _) => await RunAsync(ShowNotesAsync);
    }

    private async Task ShowNotesAsync(CrmService service)
    {
        var notes = await service.GetNotesAsync(_ticket.Id);
        if (IsDisposed) return;
        var line = Environment.NewLine;
        _notes.Text = notes.Count == 0 ? Loc.T("TicketNoNotes")
            : string.Join(line + line, notes.Select(n => $"{n.Date:yyyy-MM-dd HH:mm}  {n.Author}{line}{n.Text.Replace("\n", line)}"));
    }

    private async Task RunAsync(Func<CrmService, Task> action)
    {
        UseWaitCursor = true;
        try
        {
            using var service = await CrmService.ConnectAsync(_settings);
            await action(service);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            if (!IsDisposed) _notes.Text = ex.Message;
        }
        finally
        {
            if (!IsDisposed) UseWaitCursor = false;
        }
    }
}
