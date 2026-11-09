using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tray;

/// <summary>What the status window's buttons do (the tray app's actions; nothing in the render switch).</summary>
internal sealed record StatusActions(Action Pair, Action Activate, Func<Task> Sleep, Action OpenLogFolder, Action About);

/// <summary>
/// The window an operator opens when something is unclear (<see cref="StatusView"/>): a header in the state's colour
/// with what is going on and the one button that fits, ticks for iRacing, Race Control, the session and the pairing,
/// the last self-test step by step and the local failsafe. Details and the recent log open on request. Closing it only
/// hides it; the agent keeps running in the tray.
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly StatusActions _actions;
    private readonly Panel _header = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Panel _accent = new() { Dock = DockStyle.Left };
    private readonly Label _title = new() { AutoSize = true };
    private readonly Label _sentence = new() { AutoSize = true };
    private readonly Button _action = new() { AutoSize = true, Visible = false };
    private readonly TableLayoutPanel _rows = Grid();
    private readonly Label _selfTestSummary = new() { AutoSize = true, Margin = new Padding(3, 0, 3, 4) };
    private readonly TableLayoutPanel _steps = Grid();
    private readonly Label _details = new() { AutoSize = true, Visible = false, Margin = new Padding(3, 0, 3, 6) };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Visible = false, BorderStyle = BorderStyle.FixedSingle,
        Font = new Font(FontFamily.GenericMonospace, 8.5f), BackColor = SystemColors.Window,
    };
    private StatusView? _shown;
    private long _shownLog = -1;
    private bool _closing;

    public StatusForm(StatusActions actions)
    {
        _actions = actions;
        Text = OperatorWords.ProductName;
        Icon = TrayIcons.Program;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Window;
        Size = LogicalToDeviceUnits(new Size(600, 640));
        MinimumSize = LogicalToDeviceUnits(new Size(480, 420));
        var wrap = LogicalToDeviceUnits(500);

        _accent.Width = LogicalToDeviceUnits(6);
        _title.Font = new Font(Font.FontFamily, Font.Size * 1.6f, FontStyle.Bold);
        _sentence.MaximumSize = new Size(wrap, 0);
        _sentence.Margin = new Padding(3, 4, 3, 8);
        _action.Click += async (_, _) => await RunActionAsync();
        var headerText = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false,
            Padding = Pad(14, 12, 14, 12) };
        headerText.Controls.AddRange([_title, _sentence, _action]);
        _header.Controls.Add(headerText);
        _header.Controls.Add(_accent);

        _details.Font = new Font(FontFamily.GenericMonospace, 8.5f);
        _details.MaximumSize = new Size(wrap, 0);
        _log.Size = LogicalToDeviceUnits(new Size(540, 180));

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill,
            Padding = Pad(12, 10, 12, 6) };
        body.Controls.AddRange([
            _rows,
            Heading("Last self-test"), _selfTestSummary, _steps,
            Toggle("Show details", "Hide details", _details), _details,
            Toggle("Show the recent log", "Hide the recent log", _log), _log,
        ]);

        var about = new Button { Text = "A&bout", AutoSize = true };
        about.Click += (_, _) => _actions.About();
        var logs = new Button { Text = "Open the log &folder", AutoSize = true };
        logs.Click += (_, _) => _actions.OpenLogFolder();
        var close = new Button { Text = "&Close", AutoSize = true };
        close.Click += (_, _) => Hide();
        CancelButton = close;
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = Pad(8, 8, 8, 8), BackColor = SystemColors.Control };
        footer.Controls.AddRange([close, logs, about]);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(_header);
    }

    /// <summary>Shows a status; only what changed is redrawn, so open details and the log stay as they are.</summary>
    public void Show(AgentStatus status, RecentLog recent)
    {
        var view = StatusView.From(status);
        if (view != _shown)
        {
            Apply(view);
            _shown = view;
        }

        if (recent.Version != _shownLog)
        {
            _shownLog = recent.Version;
            _log.Text = string.Join(Environment.NewLine, recent.Lines());
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
    }

    /// <summary>For the render switch: the details and the log open.</summary>
    public void OpenDetailsAndLog()
    {
        _details.Visible = _log.Visible = true;
    }

    public void CloseForExit()
    {
        _closing = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    private void Apply(StatusView view)
    {
        var (accent, soft) = ToneColours(view.Tone);
        _accent.BackColor = accent;
        _header.BackColor = soft;
        _title.Text = view.Title;
        _sentence.Text = view.Sentence;
        _action.Visible = view.Action != StatusAction.None;
        _action.Text = StatusView.ActionText(view.Action);
        _action.Tag = view.Action;
        Fill(_rows, [.. view.Rows, view.LocalFailsafe]);
        _selfTestSummary.Text = view.SelfTestSummary;
        Fill(_steps, view.SelfTestSteps);
        _details.Text = string.Join(Environment.NewLine, view.Details);
    }

    private async Task RunActionAsync()
    {
        switch (_action.Tag)
        {
            case StatusAction.Pair:
                _actions.Pair();
                break;
            case StatusAction.Activate:
                _actions.Activate();
                break;
            case StatusAction.Sleep:
                await _actions.Sleep();
                break;
        }
    }

    /// <summary>Puts the rows into a three-column grid: the check mark, the label, the text.</summary>
    private void Fill(TableLayoutPanel grid, IReadOnlyList<StatusRow> rows)
    {
        grid.SuspendLayout();
        grid.Controls.Clear();
        grid.RowStyles.Clear();
        grid.RowCount = rows.Count;
        var wrap = LogicalToDeviceUnits(380);
        for (var i = 0; i < rows.Count; i++)
        {
            var (glyph, colour) = CheckMark(rows[i].Check);
            grid.Controls.Add(new Label { Text = glyph, ForeColor = colour, AutoSize = true, Font = new Font("Segoe UI Symbol", Font.Size * 1.1f, FontStyle.Bold), Margin = new Padding(3, 2, 6, 2) }, 0, i);
            grid.Controls.Add(new Label { Text = rows[i].Label, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 12, 3) }, 1, i);
            grid.Controls.Add(new Label { Text = rows[i].Text, AutoSize = true, MaximumSize = new Size(wrap, 0), Margin = new Padding(3, 3, 3, 3) }, 2, i);
        }

        grid.ResumeLayout();
    }

    private Padding Pad(int left, int top, int right, int bottom) =>
        new(LogicalToDeviceUnits(left), LogicalToDeviceUnits(top), LogicalToDeviceUnits(right), LogicalToDeviceUnits(bottom));

    private static TableLayoutPanel Grid() => new() { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };

    private Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold), Margin = new Padding(3, 10, 3, 4),
    };

    private static LinkLabel Toggle(string show, string hide, Control target)
    {
        var link = new LinkLabel { Text = show, AutoSize = true, Margin = new Padding(3, 10, 3, 4) };
        link.LinkClicked += (_, _) =>
        {
            target.Visible = !target.Visible;
            link.Text = target.Visible ? hide : show;
        };
        return link;
    }

    /// <summary>The tray icon's colours: a strong one for the stripe, a soft one behind the header.</summary>
    public static (Color Accent, Color Soft) ToneColours(TrayIcon tone) => tone switch
    {
        TrayIcon.Busy => (Color.FromArgb(0x2F, 0x7F, 0xE0), Color.FromArgb(0xE8, 0xF1, 0xFC)),
        TrayIcon.Ready => (Color.FromArgb(0xE0, 0xA1, 0x1B), Color.FromArgb(0xFD, 0xF4, 0xDE)),
        TrayIcon.Active => (Color.FromArgb(0x2E, 0xA0, 0x4F), Color.FromArgb(0xE4, 0xF4, 0xE8)),
        TrayIcon.Problem => (Color.FromArgb(0xD6, 0x33, 0x33), Color.FromArgb(0xFB, 0xE7, 0xE7)),
        _ => (Color.FromArgb(0x8A, 0x8F, 0x98), Color.FromArgb(0xF1, 0xF2, 0xF4)),
    };

    private static (string Glyph, Color Colour) CheckMark(StatusCheck check) => check switch
    {
        StatusCheck.Ok => ("✓", Color.FromArgb(0x2E, 0xA0, 0x4F)),
        StatusCheck.Warning => ("!", Color.FromArgb(0xB7, 0x79, 0x1F)),
        StatusCheck.Problem => ("✕", Color.FromArgb(0xD6, 0x33, 0x33)),
        _ => ("–", Color.FromArgb(0x8A, 0x8F, 0x98)),
    };
}
