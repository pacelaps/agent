using System.Diagnostics;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tray;

/// <summary>About the agent: its version, the Race Control it belongs to, and where to read more or ask.</summary>
internal sealed class AboutForm : Form
{
    public const string Guide = "https://pacelaps.com/docs/race-control/agent";
    public const string Download = "https://pacelaps.com/download";
    public const string Support = "support@pacelaps.com";
    public const string Source = "https://github.com/pacelaps/agent";

    public AboutForm(string version, Uri? server)
    {
        Text = $"About {OperatorWords.ProductName}";
        Icon = TrayIcons.Program;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var logo = new PictureBox
        {
            Image = new Icon(TrayIcons.Program, new Size(64, 64)).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = LogicalToDeviceUnits(new Size(56, 56)),
            Margin = new Padding(0, 0, 16, 0),
        };
        var name = new Label { Text = OperatorWords.ProductName, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold) };
        var lines = new Label
        {
            Text = $"Version {version}" + Environment.NewLine +
                   (server is null ? "Development build" : $"For {server.Host}") + Environment.NewLine +
                   "Types race control's commands into iRacing and reports the session to Race Control." + Environment.NewLine +
                   "Free software under the GNU GPL version 3; LICENSE.txt and THIRD-PARTY-NOTICES.txt come with it.",
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(380), 0),
            Margin = new Padding(3, 4, 3, 8),
        };
        var links = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
        links.Controls.AddRange([Link("The agent guide", Guide), Link("What's new (download page)", Download), Link("Source code", Source), Link($"Support: {Support}", $"mailto:{Support}")]);
        var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
        text.Controls.AddRange([name, lines, links]);

        var close = new Button { Text = "&Close", AutoSize = true, DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right };
        AcceptButton = CancelButton = close;

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(logo, 0, 0);
        layout.Controls.Add(text, 1, 0);
        layout.Controls.Add(close, 1, 1);
        Controls.Add(layout);
    }

    private static LinkLabel Link(string text, string target)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        return link;
    }
}
