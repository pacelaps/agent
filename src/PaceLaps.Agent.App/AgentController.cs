using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using PaceLaps.Agent.App.Shell;
using PaceLaps.Agent.App.Tray;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.App;

/// <summary>
/// Runs the agent for the app: twice a second it takes the agent's status, and the tray icon, its menu, the
/// notifications and an open window follow it. The window exists only while it is open (the WPF plan, W6).
/// </summary>
public sealed class AgentController : IDisposable
{
    private readonly AgentRuntime _runtime;
    private readonly PairingFlow _pairing;
    private readonly ConfigStore _config;
    private readonly IAutoStart _autoStart;
    private readonly Win32WindowFocus _focus;
    private readonly UpdateWatch _updates;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TrayIconSet _icons;
    private readonly TrayMenu _menu;
    private readonly TaskbarIcon _tray;
    private readonly ShellModel _shell;
    private MainWindow? _window;
    private AgentStatus? _last;
    private string? _updateShown;
    private bool _exiting;

    public AgentController(AgentRuntime runtime, RecentLog recent, PairingFlow pairing, ConfigStore config, IAutoStart autoStart,
        Win32WindowFocus focus, UpdateWatch updates, string version, ILogger logger)
    {
        _ = recent;
        _runtime = runtime;
        _pairing = pairing;
        _config = config;
        _autoStart = autoStart;
        _focus = focus;
        _updates = updates;
        _logger = logger;
        _shell = new ShellModel(version, ActAsync);
        _icons = new TrayIconSet((int)Math.Round(SystemParameters.SmallIconWidth * DpiScale()));
        _menu = new TrayMenu(new TrayActions(OpenWindow, Activate, SleepAsync, OpenSettings, OpenDownloadPage, OpenLogFolder, ExitAsync));
        _tray = new TaskbarIcon
        {
            ContextMenu = _menu.Menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
            LeftClickCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(OpenWindow),
        };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        _tray.ForceCreate(enablesEfficiencyMode: false);
        Refresh();
        _timer.Start();
        if (!_runtime.Status().Paired)
        {
            // First start: the window says what to do (the tray icon alone is easy to miss).
            OpenWindow();
        }
    }

    public void OpenWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_shell);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void Dispose()
    {
        _timer.Stop();
        _tray.Dispose();
        _icons.Dispose();
    }

    private void OpenSettings()
    {
        _shell.Screen = Screen.Settings;
        OpenWindow();
    }

    private Task ActAsync(StatusAction action)
    {
        switch (action)
        {
            case StatusAction.Activate:
                Activate();
                return Task.CompletedTask;
            case StatusAction.Sleep:
                return SleepAsync();
            default:
                // Pairing in the app comes with the first start and pairing screens; the console pairs meanwhile.
                OpenWindow();
                return Task.CompletedTask;
        }
    }

    private void Activate()
    {
        if (!TypingCanWork())
        {
            return;
        }

        _ = ActivateAsync();
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
            _tray.ShowNotification("Couldn't activate", "Something went wrong. Open the app and the log folder for details.", NotificationIcon.Error);
        }

        Refresh();
    }

    private Task SleepAsync() => _runtime.SleepAsync();

    /// <summary>
    /// Windows drops keystrokes from a normal program into an elevated one (spike 2): ask before the self-test rather
    /// than let it fail at typing.
    /// </summary>
    private bool TypingCanWork()
    {
        var sim = _focus.FindSimWindow();
        if (sim.IsNone || ProcessElevation.IsCurrentProcessElevated())
        {
            return true;
        }

        var simElevated = ProcessElevation.DescribeProcessOfWindow(sim.Value);
        return simElevated == "no" || new QuestionWindow("iRacing runs as administrator and this program doesn't.",
            "Windows would drop the agent's keystrokes. Start both the same way. Run the self-test anyway?", "Run it anyway", "Cancel").ShowDialog() == true;
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
        _tray.ToolTipText = view.Tooltip;
        _shell.Update(status);
        _ = _updates.CheckIfDueAsync();
        _menu.Show(view, _shell.Status.View!, TrayPresenter.UpdateMenuText(_updates.Offer)?.TrimEnd('.'));
        foreach (var notice in TrayPresenter.Notices(_last, status))
        {
            _tray.ShowNotification(notice.Title, notice.Text, notice.Warning ? NotificationIcon.Warning : NotificationIcon.Info);
        }

        if (TrayPresenter.UpdateNotice(_updates.Offer, _updateShown, status) is { } update)
        {
            _tray.ShowNotification(update.Title, update.Text, NotificationIcon.Info);
            _updateShown = _updates.Offer!.Version;
        }

        _last = status;
    }

    private static void OpenDownloadPage() => Process.Start(new ProcessStartInfo(UpdateWatch.DownloadPage.ToString()) { UseShellExecute = true })?.Dispose();

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(AgentLogging.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AgentLogging.LogDirectory}\"") { UseShellExecute = true })?.Dispose();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (_runtime.State != AgentState.Sleeping && new QuestionWindow("The agent is connected to Race Control.", "Exit puts it to sleep first.", "Sleep and exit", "Cancel")
                .ShowDialog() != true)
        {
            return;
        }

        _exiting = true;
        _timer.Stop();
        await _runtime.SleepAsync();
        _window?.Close();
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    private static double DpiScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }
}
