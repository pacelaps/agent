using System.Diagnostics;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;
using PaceLaps.Agent.IRacing.Win32;
using Microsoft.Extensions.Logging;
using Timer = System.Windows.Forms.Timer;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// The tray icon and its menu. Twice a second it takes the agent's status: the icon, the tooltip and the menu follow
/// it, changes become notifications, and an open status window is redrawn.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AgentRuntime _runtime;
    private readonly RecentLog _recent;
    private readonly PairingFlow _pairing;
    private readonly ConfigStore _config;
    private readonly IAutoStart _autoStart;
    private readonly Win32WindowFocus _focus;
    private readonly ILogger _logger;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _activate;
    private readonly ToolStripMenuItem _sleep;
    private readonly ToolStripMenuItem _pair;
    private readonly ToolStripMenuItem _update;
    private readonly ToolStripSeparator _updateSeparator = new() { Visible = false };
    private readonly UpdateWatch _updates;
    private readonly string _version;
    private string? _updateShown;
    private readonly Timer _timer = new() { Interval = 500 };
    private readonly SynchronizationContext _ui;
    private StatusForm? _statusForm;
    private AgentStatus? _last;
    private bool _exiting;

    public TrayContext(AgentRuntime runtime, RecentLog recent, PairingFlow pairing, ConfigStore config, IAutoStart autoStart,
        Win32WindowFocus focus, UpdateWatch updates, string version, ILogger logger)
    {
        _updates = updates;
        _version = version;
        _runtime = runtime;
        _recent = recent;
        _pairing = pairing;
        _config = config;
        _autoStart = autoStart;
        _focus = focus;
        _logger = logger;
        _ui = SynchronizationContext.Current!;
        _activate = new ToolStripMenuItem("&Activate (run the self-test)", null, (_, _) => Activate());
        _sleep = new ToolStripMenuItem("S&leep (disconnect from Race Control)", null, async (_, _) => await _runtime.SleepAsync());
        _update = new ToolStripMenuItem("", null, (_, _) => ShowUpdate()) { Visible = false };
        _pair = new ToolStripMenuItem("&Pair with Race Control...", null, (_, _) => ShowPairing());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            _update,
            _updateSeparator,
            new ToolStripMenuItem("&Status...", null, (_, _) => ShowStatus()) { Font = new Font(menu.Font, FontStyle.Bold) },
            new ToolStripSeparator(),
            _activate,
            _sleep,
            new ToolStripSeparator(),
            _pair,
            new ToolStripMenuItem("S&ettings...", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Open the &log folder", null, (_, _) => OpenLogFolder()),
            new ToolStripMenuItem($"A&bout {OperatorWords.ProductName}", null, (_, _) => ShowAbout()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("E&xit", null, async (_, _) => await ExitAsync()),
        ]);
        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowStatus();
            }
        };
        _timer.Tick += (_, _) => Refresh();
        Refresh();
        _timer.Start();
    }

    /// <summary>Another start of the program asked for the status window (any thread).</summary>
    public void ShowStatusFromAnotherStart() => _ui.Post(_ => ShowStatus(), null);

    public void Activate()
    {
        if (!TypingCanWork())
        {
            return;
        }

        _ = ActivateAsync();
    }

    public Task SleepAsync() => _runtime.SleepAsync();

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(AgentLogging.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AgentLogging.LogDirectory}\"") { UseShellExecute = true })?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icons.Dispose();
            _statusForm?.Dispose();
            _updates.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task ActivateAsync()
    {
        try
        {
            await _runtime.ActivateAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Activate failed");
            _tray.ShowBalloonTip(5000, "Couldn't activate", "Something went wrong. Open the status window and the log folder for details.", ToolTipIcon.Error);
        }

        Refresh();
    }

    /// <summary>
    /// Windows drops keystrokes from a normal program into an elevated one (spike 2): warn before the self-test
    /// rather than let it fail at the typed channel.
    /// </summary>
    private bool TypingCanWork()
    {
        var sim = _focus.FindSimWindow();
        if (sim.IsNone || ProcessElevation.IsCurrentProcessElevated())
        {
            return true;
        }

        var simElevated = ProcessElevation.DescribeProcessOfWindow(sim.Value);
        return simElevated == "no" || MessageBox.Show(
            $"iRacing runs as administrator ({simElevated}) and this program doesn't, so Windows would drop its keystrokes.\n\n" +
            "Run both the same way. Run the self-test anyway?",
            OperatorWords.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void Refresh()
    {
        AgentStatus status;
        try
        {
            status = _runtime.Status();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The settings file is being written; next time.
            return;
        }

        var view = TrayPresenter.Present(status);
        _tray.Icon = _icons[view.Icon];
        _tray.Text = view.Tooltip;
        _activate.Enabled = view.CanActivate;
        _sleep.Enabled = view.CanSleep;
        _pair.Enabled = view.CanPair;
        foreach (var notice in TrayPresenter.Notices(_last, status))
        {
            _tray.ShowBalloonTip(5000, notice.Title, notice.Text, notice.Warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        RefreshUpdate(status);

        _last = status;
        if (_statusForm is { Visible: true } form)
        {
            form.Show(status, view, _recent);
        }
    }

    /// <summary>The update notice: a check when one is due, the menu line, and one notification per version.</summary>
    private void RefreshUpdate(AgentStatus status)
    {
        _ = _updates.CheckIfDueAsync();
        var offer = _updates.Offer;
        var text = TrayPresenter.UpdateMenuText(offer);
        _update.Visible = _updateSeparator.Visible = text is not null;
        _update.Text = text ?? "";
        if (TrayPresenter.UpdateNotice(offer, _updateShown, status) is { } notice)
        {
            _tray.ShowBalloonTip(5000, notice.Title, notice.Text, ToolTipIcon.Info);
            _updateShown = offer!.Version;
        }
    }

    private void ShowUpdate()
    {
        if (_updates.Offer is { } offer)
        {
            using var form = new UpdateForm(offer, _version);
            form.ShowDialog();
        }
    }

    private void ShowStatus()
    {
        _statusForm ??= new StatusForm(this);
        _statusForm.Show(_runtime.Status(), TrayPresenter.Present(_runtime.Status()), _recent);
        _statusForm.Show();
        _statusForm.WindowState = FormWindowState.Normal;
        _statusForm.Activate();
    }

    private void ShowAbout()
    {
        using var form = new AboutForm(_version, _pairing.FixedServer);
        form.ShowDialog();
    }

    private void ShowPairing()
    {
        using var form = new PairForm(_pairing);
        form.ShowDialog();
        Refresh();
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_config, _autoStart, _runtime.State != AgentState.Sleeping, _pairing.FixedServer);
        form.ShowDialog();
        Refresh();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (_runtime.State != AgentState.Sleeping && MessageBox.Show(
                "The agent is connected to Race Control. Exit puts it to sleep first. Exit?",
                OperatorWords.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        _exiting = true;
        _timer.Stop();
        await _runtime.SleepAsync();
        _statusForm?.CloseForExit();
        _tray.Visible = false;
        ExitThread();
    }
}
