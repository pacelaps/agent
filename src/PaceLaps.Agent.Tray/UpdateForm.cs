using System.Diagnostics;
using PaceLaps.Agent.Core.Updates;
using PaceLaps.Agent.Hosting;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// The update notice's window: the new version and what changed, with a button to the download page. The agent
/// downloads and installs nothing itself; the operator replaces the program as the operator guide says.
/// </summary>
internal sealed class UpdateForm : Form
{
    public UpdateForm(UpdateOffer offer, string currentVersion)
    {
        Text = "PaceLaps Agent update";
        Icon = TrayIcons.Program;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var title = new Label { Text = $"Version {offer.Version} is available (you have {currentVersion}).", AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        var changes = new Label
        {
            Text = offer.Lines.Count == 0 ? "The download page lists what's new." : string.Join(Environment.NewLine, offer.Lines.Select(l => "• " + l)),
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(3, 8, 3, 8),
        };
        var how = new Label
        {
            Text = "Put the agent to sleep, exit it, and replace pacelaps-agent.exe with the new one. Your pairing and settings stay.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = SystemColors.GrayText,
        };
        var open = new Button { Text = "&Open the download page", AutoSize = true };
        open.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo(UpdateWatch.DownloadPage.ToString()) { UseShellExecute = true })?.Dispose();
            Close();
        };
        var later = new Button { Text = "&Not now", AutoSize = true, DialogResult = DialogResult.Cancel };
        AcceptButton = open;
        CancelButton = later;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([later, open]);
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.AddRange([title, changes, how, buttons]);
        Controls.Add(layout);
    }
}
