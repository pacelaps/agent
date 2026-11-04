using System.Diagnostics;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// The pairing screen: the server's address, then the code in large type and where to enter it. A steward approves
/// the code on the web page; the credentials are stored for this Windows user.
/// </summary>
internal sealed class PairForm : Form
{
    private readonly PairingFlow _flow;
    private readonly TextBox _server = new() { Width = 360 };
    private readonly Button _start = new() { Text = "&Get a code", AutoSize = true };
    private readonly Label _code = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 28f, FontStyle.Bold), Text = "" };
    private readonly LinkLabel _link = new() { AutoSize = true, Visible = false };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(460, 0), Text = "" };
    private readonly Button _close = new() { Text = "&Cancel", AutoSize = true };
    private CancellationTokenSource? _waiting;

    public PairForm(PairingFlow flow)
    {
        _flow = flow;
        Text = "Pair with PaceLaps Race Control";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        _server.Text = flow.SuggestedServer();
        _server.ReadOnly = flow.FixedServer is not null;
        _start.Click += async (_, _) => await PairAsync();
        _close.Click += (_, _) =>
        {
            _waiting?.Cancel();
            Close();
        };
        _link.LinkClicked += (_, _) => OpenInBrowser(_link.Tag as string);
        AcceptButton = _start;

        var serverRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        serverRow.Controls.AddRange([_server, _start]);
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = "Server address (as on the race control page):", AutoSize = true });
        layout.Controls.Add(serverRow);
        layout.Controls.Add(new Label
        {
            Text = "Open the page below as a steward and enter the code.",
            AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 12, 3, 3),
        });
        layout.Controls.Add(_code);
        layout.Controls.Add(_link);
        layout.Controls.Add(_message);
        layout.Controls.Add(_close);
        Controls.Add(layout);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _waiting?.Cancel();
        _waiting?.Dispose();
        base.OnFormClosed(e);
    }

    private async Task PairAsync()
    {
        _start.Enabled = _server.Enabled = false;
        _message.Text = "Asking the server for a code...";
        _waiting = new CancellationTokenSource();
        var outcome = await _flow.PairAsync(_server.Text, code => BeginInvoke(() => ShowCode(code)), _waiting.Token);
        if (IsDisposed)
        {
            return;
        }

        _message.Text = outcome.Message;
        _message.ForeColor = outcome.Paired ? Color.DarkGreen : Color.DarkRed;
        _close.Text = "&Close";
        if (!outcome.Paired)
        {
            _start.Enabled = _server.Enabled = true;
            _code.Text = "";
            _link.Visible = false;
        }
    }

    private void ShowCode(PairingCodeMessage code)
    {
        _code.Text = code.UserCode;
        _link.Text = code.VerificationUrl;
        _link.Tag = code.VerificationUrl;
        _link.Visible = true;
        _message.Text = "Waiting for the approval" + (code.ExpiresAt is { } until ? $" (until {until.ToLocalTime():HH:mm})." : ".");
    }

    /// <summary>Only a web address the server could mean (https, or http on this PC), never anything else.</summary>
    private static void OpenInBrowser(string? url)
    {
        if (url is not null && ServerAddress.TryParse(url, out _, out _) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    }
}
