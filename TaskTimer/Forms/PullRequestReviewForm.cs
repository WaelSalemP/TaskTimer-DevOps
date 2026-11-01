using TaskTimer.Models;
using TaskTimer.Services;

namespace TaskTimer.Forms;

/// <summary>
/// Reviews a pull request: changed files on the left, the selected file's differences on the right
/// (with existing comments shown under their lines), and actions to comment and vote.
/// </summary>
public sealed class PullRequestReviewForm : Form
{
    private readonly AppSettings _settings;
    private readonly PullRequest _pr;
    private readonly Dictionary<string, List<DiffLine>> _diffs = new();
    private List<PullRequestComment> _comments = new();
    private string _newCommit = "";
    private string _baseCommit = "";
    private int _loadVersion;

    private readonly ListBox _files = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly DataGridView _diff = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, ColumnHeadersVisible = false
    };
    private readonly Label _summary = new() { AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
    private readonly Label _myVote = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
    private readonly Font _code = new("Consolas", 9.75f);
    private readonly Font _commentFont = new("Segoe UI", 9f, FontStyle.Italic);

    /// <summary>The overview entry at the top of the file list: general comments.</summary>
    private const string Overview = "\u0001overview";

    private enum ViewMode { Inline, SideBySide, OldVersion }

    /// <summary>One row of the side-by-side view: the old line on the left, the new line on the right.</summary>
    private sealed record SidePair(DiffLine? Old, DiffLine? New)
    {
        public bool IsChange => Old?.Kind == DiffKind.Removed || New?.Kind == DiffKind.Added;
    }

    // Kept for the session, so the next review opens in the view the user chose last.
    private static ViewMode _lastView = ViewMode.Inline;
    private readonly ComboBox _view = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Margin = new Padding(3, 4, 3, 3) };
    private ViewMode? _columnsFor;
    private readonly bool _voteLevels;

    /// <param name="approveBlockedReason">When set, required checks have failed: approving is not offered and this says why.</param>
    public PullRequestReviewForm(AppSettings settings, PullRequest pr, string? approveBlockedReason = null)
    {
        _settings = settings;
        _pr = pr;

        Icon = Program.AppIcon;
        Text = Loc.T("PrReviewTitle", pr.Id, pr.Title);
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1300, 760);
        MinimumSize = new Size(800, 450);
        Loc.ApplyDirection(this);
        Theme.StyleForm(this, Theme.Surface);
        Theme.StyleGrid(_diff);
        _diff.RowTemplate.Height = 20;
        _diff.CellBorderStyle = DataGridViewCellBorderStyle.None;
        _diff.DefaultCellStyle.Padding = new Padding(2, 0, 2, 0);
        SetupColumns(ViewMode.Inline);
        _diff.DefaultCellStyle.Font = _code;
        _diff.ScrollBars = ScrollBars.Both;
        _diff.SizeChanged += (_, _) => SizeCodeColumns();
        // Code reads left to right whatever the interface language is.
        _diff.RightToLeft = RightToLeft.No;

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        // Set once the window has its real size; earlier, the container is too small and clamps it.
        Load += (_, _) => split.SplitterDistance = Math.Min(LogicalToDeviceUnits(380), split.Width / 2);
        split.Panel1.Controls.Add(_files);
        split.Panel2.Controls.Add(_diff);

        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 0) };
        _summary.Text = $"{pr.RepositoryName}:  {pr.SourceBranch} → {pr.TargetBranch}   ({pr.CreatedBy})";
        _view.Items.AddRange(new object[] { Loc.T("PrViewInline"), Loc.T("PrViewSideBySide"), Loc.T("PrViewOld") });
        _view.SelectedIndex = (int)_lastView;
        _view.SelectedIndexChanged += async (_, _) =>
        {
            _lastView = CurrentView;
            await RefreshCurrentViewAsync();
        };
        header.Controls.AddRange(new Control[]
        {
            _summary, _myVote, new Label { Text = Loc.T("PrView"), AutoSize = true, Margin = new Padding(24, 8, 3, 3) }, _view
        });

        var previous = NewButton("PrPrevChange", Theme.Neutral);
        var next = NewButton("PrNextChange", Theme.Neutral);
        var comment = NewButton("PrCommentLine", Theme.Primary);
        var general = NewButton("PrCommentGeneral", Theme.Neutral);
        var approve = NewButton("PrApprove", Theme.Green);
        var suggestions = NewButton("PrVoteSuggestions", Theme.Green);
        approve.Enabled = suggestions.Enabled = approveBlockedReason == null;
        if (approveBlockedReason != null)
            header.Controls.Add(new Label { Text = approveBlockedReason, AutoSize = true, ForeColor = Theme.Coral, Margin = new Padding(24, 8, 3, 3) });
        var wait = NewButton("PrVoteWaiting", Theme.Neutral);
        var reject = NewButton("PrVoteRejected", Theme.Coral);
        var browser = NewButton("OpenInBrowser", Theme.Neutral);
        var workItems = NewButton("PrWorkItems", Theme.Neutral);
        // GitHub reviews only approve or request changes, and pull requests have no linked work items.
        var features = PullRequestServices.Features(settings);
        _voteLevels = features.VoteLevels;
        suggestions.Visible = wait.Visible = features.VoteLevels;
        workItems.Visible = features.WorkItems;
        if (!features.VoteLevels) reject.Text = Loc.T("PrRequestChanges");
        workItems.Click += (_, _) => LinkedWorkItemsForm.Show(this, settings, pr);
        var close = NewButton("MachineClose", Theme.Neutral);
        close.DialogResult = DialogResult.OK;
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        bottom.Controls.AddRange(new Control[] { previous, next, comment, general, approve, suggestions, wait, reject, workItems, browser, close });

        Controls.Add(split);
        Controls.Add(header);
        Controls.Add(bottom);
        CancelButton = close;

        _files.SelectedIndexChanged += async (_, _) => await ShowSelectedFileAsync();
        previous.Click += (_, _) => JumpToChange(forward: false);
        next.Click += (_, _) => JumpToChange(forward: true);
        comment.Click += async (_, _) => await CommentOnLineAsync();
        _diff.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await CommentOnLineAsync(); };
        general.Click += async (_, _) => await AddCommentAsync(null, 0, false);
        approve.Click += async (_, _) => await VoteAsync(10);
        suggestions.Click += async (_, _) => await VoteAsync(5);
        wait.Click += async (_, _) => await VoteAsync(-5);
        reject.Click += async (_, _) => await VoteAsync(-10);
        browser.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pr.WebUrl) { UseShellExecute = true });
        Shown += async (_, _) => await LoadAsync();
        UpdateVoteLabel(pr.MyVote);
    }

    private static Button NewButton(string key, Color color)
    {
        var button = new Button { Text = Loc.T(key), AutoSize = true, Padding = new Padding(6, 3, 6, 3) };
        Theme.StyleButton(button, color);
        return button;
    }

    private void UpdateVoteLabel(int vote) => _myVote.Text = Loc.T("PrColMyVote") + ": " + vote switch
    {
        10 => Loc.T("PrVoteApproved"),
        5 => Loc.T("PrVoteSuggestions"),
        -5 => Loc.T("PrVoteWaiting"),
        -10 => Loc.T(_voteLevels ? "PrVoteRejected" : "PrChangesRequested"),
        _ => Loc.T("PrVoteNone")
    };

    private void ShowError(Exception ex) =>
        MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1, Loc.MsgOptions);

    private async Task LoadAsync()
    {
        UseWaitCursor = true;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            var (files, newCommit, baseCommit) = await service.GetChangesAsync(_pr);
            _newCommit = newCommit;
            _baseCommit = baseCommit;
            _comments = await service.GetCommentsAsync(_pr);
            if (IsDisposed) return;

            _files.Items.Clear();
            _files.Items.Add(Overview);
            _files.Items.AddRange(files.Cast<object>().ToArray());
            _files.Format += (_, e) =>
            {
                if (e.ListItem as string == Overview)
                    e.Value = Loc.T("PrOverview", _comments.Count(c => c.FilePath.Length == 0));
                else if (e.ListItem is PullRequestFileChange file)
                    e.Value = file + (_comments.Count(c => c.FilePath == file.Path) is var n and > 0 ? $"   💬{n}" : "");
            };
            _files.FormattingEnabled = true;
            _files.SelectedIndex = files.Count > 0 ? 1 : 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task ShowSelectedFileAsync()
    {
        var version = ++_loadVersion;
        _diff.Rows.Clear();
        if (_files.SelectedItem is string)
        {
            ShowOverview();
            return;
        }
        if (_files.SelectedItem is not PullRequestFileChange file) return;

        if (!_diffs.TryGetValue(file.Path, out var lines))
        {
            AddMessageRow(Loc.T("PrLoading"));
            try
            {
                using var service = PullRequestServices.Create(_settings);
                var newText = file.IsDelete ? "" : await service.GetFileTextAsync(_pr, file.Path, _newCommit);
                var oldText = file.IsAdd ? "" : await service.GetFileTextAsync(_pr, file.OriginalPath, _baseCommit);
                if (version != _loadVersion || IsDisposed) return;
                if (newText == null || oldText == null)
                {
                    _diff.Rows.Clear();
                    AddMessageRow(Loc.T("PrBinary"));
                    return;
                }
                var oldLines = LineDiff.SplitLines(oldText);
                var newLines = LineDiff.SplitLines(newText);
                if (oldLines.Length + newLines.Length > LineDiff.MaxLines)
                {
                    _diff.Rows.Clear();
                    AddMessageRow(Loc.T("PrTooLarge"));
                    return;
                }
                lines = LineDiff.Compute(oldLines, newLines);
                _diffs[file.Path] = lines;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                if (version != _loadVersion || IsDisposed) return;
                _diff.Rows.Clear();
                AddMessageRow(ex.Message);
                return;
            }
        }
        if (version != _loadVersion) return;
        RenderDiff(file, lines);
        JumpToChange(forward: true, fromTop: true);
    }

    private ViewMode CurrentView => (ViewMode)Math.Max(0, _view.SelectedIndex);

    private DataGridViewTextBoxColumn NumberColumn(string name) => new()
    {
        Name = name, Width = LogicalToDeviceUnits(56),
        DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight, ForeColor = Theme.Muted }
    };

    private DataGridViewTextBoxColumn MarkColumn() => new() { Name = "Mark", Width = LogicalToDeviceUnits(22) };

    // Code columns get their width from SizeCodeColumns: as wide as the longest line, so long lines
    // scroll sideways instead of being cut off.
    private static DataGridViewTextBoxColumn CodeColumn(string name) => new() { Name = name };

    /// <summary>
    /// Makes each code column at least its share of the visible width, and wider when its longest
    /// line needs it; the grid then shows a horizontal scrollbar.
    /// </summary>
    private void SizeCodeColumns()
    {
        var codeColumns = new[] { "OldCode", "Code" }.Where(n => _diff.Columns.Contains(n)).Select(n => _diff.Columns[n]!).ToList();
        if (codeColumns.Count == 0) return;

        var otherColumns = _diff.Columns.Cast<DataGridViewColumn>().Except(codeColumns).Sum(c => c.Width);
        var scrollBar = _diff.Controls.OfType<VScrollBar>().FirstOrDefault(s => s.Visible)?.Width ?? 0;
        var share = Math.Max(LogicalToDeviceUnits(120), (_diff.ClientSize.Width - otherColumns - scrollBar - 2) / codeColumns.Count);
        // The code font is monospaced, so the longest line in characters is the widest one.
        var charWidth = TextRenderer.MeasureText(new string('M', 20), _code).Width / 20.0;
        foreach (var column in codeColumns)
        {
            var longest = _diff.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is not PullRequestComment) // comments use a narrower font and may simply be cut
                .Select(r => (r.Cells[column.Index].Value as string)?.Length ?? 0)
                .DefaultIfEmpty(0).Max();
            column.Width = Math.Clamp((int)(longest * charWidth) + LogicalToDeviceUnits(16), share, 60000);
        }
    }

    /// <summary>Empties the grid and lays it out for a view. Every layout has a "Code" column (the new side).</summary>
    private void SetupColumns(ViewMode view)
    {
        _diff.Rows.Clear();
        if (_columnsFor == view) return;
        _diff.Columns.Clear();
        _diff.Columns.AddRange(view switch
        {
            ViewMode.SideBySide => new DataGridViewColumn[] { NumberColumn("Old"), CodeColumn("OldCode"), NumberColumn("New"), CodeColumn("Code") },
            ViewMode.OldVersion => new DataGridViewColumn[] { NumberColumn("Old"), MarkColumn(), CodeColumn("Code") },
            _ => new DataGridViewColumn[] { NumberColumn("Old"), NumberColumn("New"), MarkColumn(), CodeColumn("Code") }
        });
        _columnsFor = view;
    }

    private int Column(string name) => _diff.Columns[name]!.Index;

    /// <summary>A grid row (not yet added) with values for the named columns; other cells stay empty.</summary>
    private DataGridViewRow NewRow(object tag, params (string Column, object Value)[] values)
    {
        var row = new DataGridViewRow();
        row.CreateCells(_diff);
        foreach (var (column, value) in values) row.Cells[Column(column)].Value = value;
        row.Tag = tag;
        return row;
    }

    private void RenderDiff(PullRequestFileChange file, List<DiffLine> lines)
    {
        var view = CurrentView;
        _diff.SuspendLayout();
        SetupColumns(view);
        var comments = _comments.Where(c => c.FilePath == file.Path).ToList();
        var rows = view switch
        {
            ViewMode.SideBySide => SideBySideRows(lines, comments),
            ViewMode.OldVersion => OldVersionRows(lines, comments),
            _ => InlineRows(lines, comments)
        };
        _diff.Rows.AddRange(rows.ToArray());
        if (rows.Count == 0) AddMessageRow(Loc.T(view == ViewMode.OldVersion ? "PrNoOldVersion" : "PrEmptyFile"));
        SizeCodeColumns();
        _diff.ResumeLayout();
    }

    private static string Code(DiffLine line) => line.Text.Replace("\t", "    ");

    private static string Mark(DiffLine line) => line.Kind switch { DiffKind.Added => "+", DiffKind.Removed => "−", _ => "" };

    // Comments sit under the line they were written on; comments on the old side belong to old lines.
    private static IEnumerable<PullRequestComment> CommentsOnOld(List<PullRequestComment> comments, DiffLine? line) =>
        line is { Kind: not DiffKind.Added } ? comments.Where(c => c.LeftSide && c.Line == line.OldNumber) : Enumerable.Empty<PullRequestComment>();

    private static IEnumerable<PullRequestComment> CommentsOnNew(List<PullRequestComment> comments, DiffLine? line) =>
        line is { Kind: not DiffKind.Removed } ? comments.Where(c => !c.LeftSide && c.Line == line.NewNumber) : Enumerable.Empty<PullRequestComment>();

    /// <summary>One column of code with removed and added lines one after the other.</summary>
    private List<DataGridViewRow> InlineRows(List<DiffLine> lines, List<PullRequestComment> comments)
    {
        var rows = new List<DataGridViewRow>(lines.Count + comments.Count);
        foreach (var line in lines)
        {
            var row = NewRow(line, ("Old", line.OldNumber?.ToString() ?? ""), ("New", line.NewNumber?.ToString() ?? ""),
                ("Mark", Mark(line)), ("Code", Code(line)));
            if (line.Kind != DiffKind.Same) row.DefaultCellStyle.BackColor = line.Kind == DiffKind.Added ? Theme.DiffAdded : Theme.DiffRemoved;
            rows.Add(row);
            rows.AddRange(CommentsOnOld(comments, line).Concat(CommentsOnNew(comments, line)).Select(c => CommentRow(c, "Code")));
        }
        return rows;
    }

    /// <summary>The old version on the left and the new one on the right; within a changed block, old and new lines are paired up.</summary>
    private List<DataGridViewRow> SideBySideRows(List<DiffLine> lines, List<PullRequestComment> comments)
    {
        var rows = new List<DataGridViewRow>(lines.Count + comments.Count);
        void Add(SidePair pair)
        {
            var row = NewRow(pair,
                ("Old", pair.Old?.OldNumber?.ToString() ?? ""), ("OldCode", pair.Old == null ? "" : Code(pair.Old)),
                ("New", pair.New?.NewNumber?.ToString() ?? ""), ("Code", pair.New == null ? "" : Code(pair.New)));
            if (pair.IsChange)
            {
                // Each side is coloured on its own; the empty side of an unpaired line stays plain.
                var oldBack = pair.Old != null ? Theme.DiffRemoved : Theme.Background;
                var newBack = pair.New != null ? Theme.DiffAdded : Theme.Background;
                row.Cells[Column("Old")].Style.BackColor = row.Cells[Column("OldCode")].Style.BackColor = oldBack;
                row.Cells[Column("New")].Style.BackColor = row.Cells[Column("Code")].Style.BackColor = newBack;
            }
            rows.Add(row);
            rows.AddRange(CommentsOnOld(comments, pair.Old).Select(c => CommentRow(c, "OldCode")));
            rows.AddRange(CommentsOnNew(comments, pair.New).Select(c => CommentRow(c, "Code")));
        }

        var removed = new List<DiffLine>();
        var added = new List<DiffLine>();
        void FlushBlock()
        {
            for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
                Add(new SidePair(i < removed.Count ? removed[i] : null, i < added.Count ? added[i] : null));
            removed.Clear();
            added.Clear();
        }

        foreach (var line in lines)
        {
            if (line.Kind == DiffKind.Removed) removed.Add(line);
            else if (line.Kind == DiffKind.Added) added.Add(line);
            else
            {
                FlushBlock();
                Add(new SidePair(line, line));
            }
        }
        FlushBlock();
        return rows;
    }

    /// <summary>The file as it was before the pull request, with the lines it removes or changes highlighted.</summary>
    private List<DataGridViewRow> OldVersionRows(List<DiffLine> lines, List<PullRequestComment> comments)
    {
        var rows = new List<DataGridViewRow>();
        foreach (var line in lines.Where(l => l.Kind != DiffKind.Added))
        {
            var row = NewRow(line, ("Old", line.OldNumber?.ToString() ?? ""), ("Mark", Mark(line)), ("Code", Code(line)));
            if (line.Kind == DiffKind.Removed) row.DefaultCellStyle.BackColor = Theme.DiffRemoved;
            rows.Add(row);
            rows.AddRange(CommentsOnOld(comments, line).Select(c => CommentRow(c, "Code")));
        }
        return rows;
    }

    private DataGridViewRow CommentRow(PullRequestComment comment, string column)
    {
        var when = comment.Date?.ToString("yyyy-MM-dd HH:mm") ?? "";
        var status = comment.Status.Length > 0 ? ", " + comment.Status : "";
        var row = NewRow(comment, (column, $"💬 {comment.Author} ({when}{status}): {comment.Text.Replace("\r", "").Replace("\n", "  ")}"));
        row.DefaultCellStyle.BackColor = Theme.DiffComment;
        row.DefaultCellStyle.Font = _commentFont;
        return row;
    }

    private void ShowOverview()
    {
        SetupColumns(ViewMode.Inline);
        var general = _comments.Where(c => c.FilePath.Length == 0).ToList();
        if (general.Count == 0) AddMessageRow(Loc.T("PrNoGeneralComments"));
        foreach (var comment in general) _diff.Rows.Add(CommentRow(comment, "Code"));
        SizeCodeColumns();
    }

    private void AddMessageRow(string text)
    {
        var row = NewRow("", ("Code", text));
        row.DefaultCellStyle.ForeColor = Theme.Muted;
        _diff.Rows.Add(row);
        SizeCodeColumns();
    }

    /// <summary>Moves to the first line of the next (or previous) block of changes.</summary>
    private void JumpToChange(bool forward, bool fromTop = false)
    {
        bool IsChange(int i) => _diff.Rows[i].Tag is DiffLine { Kind: not DiffKind.Same } or SidePair { IsChange: true };
        bool StartsBlock(int i) => IsChange(i) && (i == 0 || !IsChange(i - 1));
        var current = fromTop ? -1 : _diff.CurrentRow?.Index ?? -1;
        var range = forward
            ? Enumerable.Range(current + 1, Math.Max(0, _diff.Rows.Count - current - 1))
            : Enumerable.Range(0, Math.Max(0, current)).Reverse();
        foreach (var i in range)
        {
            if (!StartsBlock(i)) continue;
            _diff.CurrentCell = _diff.Rows[i].Cells[Column("Code")];
            // Leave a few lines of context above the change, and start at the left edge: selecting
            // the new-side cell would otherwise scroll the old side out of view.
            _diff.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 3);
            _diff.HorizontalScrollingOffset = 0;
            return;
        }
    }

    private async Task CommentOnLineAsync()
    {
        var clickedColumn = _diff.CurrentCell == null ? "" : _diff.Columns[_diff.CurrentCell.ColumnIndex].Name;
        (int Line, bool LeftSide)? target = _diff.CurrentRow?.Tag switch
        {
            // Lines of the old version, and removed lines, only exist on the old (left) side.
            DiffLine line when CurrentView == ViewMode.OldVersion || line.Kind == DiffKind.Removed => (line.OldNumber!.Value, true),
            DiffLine line => (line.NewNumber!.Value, false),
            // Side by side, the side that was clicked decides; a side without a line falls back to the other.
            SidePair pair when pair.New == null || (clickedColumn is "Old" or "OldCode" && pair.Old != null) => (pair.Old!.OldNumber!.Value, true),
            SidePair pair => (pair.New!.NewNumber!.Value, false),
            _ => null
        };
        if (_files.SelectedItem is not PullRequestFileChange file || target == null)
        {
            MessageBox.Show(this, Loc.T("PrPickLine"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, Loc.MsgOptions);
            return;
        }
        await AddCommentAsync(file.Path, target.Value.Line, target.Value.LeftSide);
    }
    private async Task AddCommentAsync(string? filePath, int line, bool leftSide)
    {
        var prompt = filePath == null ? Loc.T("PrCommentGeneral") : Loc.T("PrCommentOn", Path.GetFileName(filePath), line);
        var text = CommentPrompt.Ask(this, prompt);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            using var service = PullRequestServices.Create(_settings);
            await service.AddCommentAsync(_pr, text.Trim(), filePath, line, leftSide);
            _comments = await service.GetCommentsAsync(_pr);
            _files.Refresh();
            await RefreshCurrentViewAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex);
        }
    }

    private async Task RefreshCurrentViewAsync()
    {
        var keepRow = _diff.FirstDisplayedScrollingRowIndex;
        var current = _diff.CurrentRow?.Index ?? 0;
        if (_files.SelectedItem is PullRequestFileChange file && _diffs.TryGetValue(file.Path, out var lines)) RenderDiff(file, lines);
        else await ShowSelectedFileAsync();
        if (_diff.Rows.Count == 0) return;
        _diff.FirstDisplayedScrollingRowIndex = Math.Clamp(keepRow, 0, _diff.Rows.Count - 1);
        _diff.CurrentCell = _diff.Rows[Math.Clamp(current, 0, _diff.Rows.Count - 1)].Cells[Column("Code")];
        _diff.HorizontalScrollingOffset = 0;
    }

    private async Task VoteAsync(int vote)
    {
        // GitHub requires a comment saying what has to change.
        string? comment = null;
        if (vote < 0 && !_voteLevels)
        {
            comment = CommentPrompt.Ask(this, Loc.T("PrRequestChanges"));
            if (string.IsNullOrWhiteSpace(comment)) return;
        }
        try
        {
            using var service = PullRequestServices.Create(_settings);
            await service.VoteAsync(_pr, vote, comment?.Trim());
            UpdateVoteLabel(vote);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex);
        }
    }
}

/// <summary>A small multi-line text prompt for writing a review comment.</summary>
internal static class CommentPrompt
{
    public static string? Ask(IWin32Window owner, string title)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(520, 230),
            Icon = Program.AppIcon, AutoScaleMode = AutoScaleMode.Font
        };
        Loc.ApplyDirection(form);
        Theme.StyleForm(form, Theme.Surface);
        var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        var post = new Button { Text = Loc.T("PrPostComment"), AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(10, 3, 10, 3) };
        var cancel = new Button { Text = Loc.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 3, 8, 3) };
        Theme.StyleButton(post, Theme.Primary);
        Theme.StyleButton(cancel, Theme.Neutral);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        buttons.Controls.AddRange(new Control[] { cancel, post });
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        body.Controls.Add(text);
        form.Controls.Add(body);
        form.Controls.Add(buttons);
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? text.Text : null;
    }
}
