using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// Everything about the agent: its status, the last self-test with what failed, and the recent log lines. Closing it
/// only hides it; the agent keeps running in the tray.
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly TextBox _status = ReadOnlyBox();
    private readonly TextBox _selfTest = ReadOnlyBox();
    private readonly TextBox _log = ReadOnlyBox();
    private readonly Button _activate = new() { Text = "&Activate", AutoSize = true };
    private readonly Button _sleep = new() { Text = "S&leep", AutoSize = true };
    private string _shownStatus = "";
    private string _shownSelfTest = "";
    private long _shownLog = -1;
    private bool _closing;

    public StatusForm(TrayContext tray)
    {
        Text = "PaceLaps Race Control";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 640);
        MinimumSize = new Size(520, 420);
        _activate.Click += (_, _) => tray.Activate();
        _sleep.Click += async (_, _) => await tray.SleepAsync();
        var logs = new Button { Text = "Open the log &folder", AutoSize = true };
        logs.Click += (_, _) => tray.OpenLogFolder();
        var close = new Button { Text = "&Close", AutoSize = true };
        close.Click += (_, _) => Hide();
        CancelButton = close;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange([_activate, _sleep, logs, close]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Heading("Status"));
        layout.Controls.Add(_status);
        layout.Controls.Add(Heading("Last self-test"));
        layout.Controls.Add(_selfTest);
        layout.Controls.Add(Heading("Recent log (everything is in the log folder)"));
        layout.Controls.Add(_log);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    public void Show(AgentStatus status, TrayView view, RecentLog recent)
    {
        _activate.Enabled = view.CanActivate;
        _sleep.Enabled = view.CanSleep;
        Set(_status, ref _shownStatus, string.Join(Environment.NewLine, status.Describe()));
        Set(_selfTest, ref _shownSelfTest, status.LastSelfTest is { } report
            ? (report.Passed ? "Passed." : "FAILED. The agent went back to sleep.") + Environment.NewLine + string.Join(Environment.NewLine, AgentStatus.DescribeSteps(report))
            : "None yet. Activate runs it.");
        if (recent.Version != _shownLog)
        {
            _shownLog = recent.Version;
            _log.Text = string.Join(Environment.NewLine, recent.Lines());
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }
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

    private static void Set(TextBox box, ref string shown, string text)
    {
        if (text != shown)
        {
            shown = text;
            box.Text = text;
        }
    }

    private static TextBox ReadOnlyBox() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = SystemColors.Window,
    };

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? DefaultFont, FontStyle.Bold), Margin = new Padding(0, 6, 0, 2),
    };
}
