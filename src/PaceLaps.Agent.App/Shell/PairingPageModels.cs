using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Shell;

/// <summary>The first start (the design canvas's "First start"): three steps, Pair, and Start with Windows.</summary>
public sealed partial class WelcomePageModel(IAutoStart autoStart, Action pair) : ObservableObject
{
    public const string Guide = "https://pacelaps.com/docs/race-control/agent";

    public bool StartWithWindows
    {
        get => autoStart.Enabled;
        set
        {
            autoStart.Set(value);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void Pair() => pair();

    [RelayCommand]
    private static void OpenGuide() => Process.Start(new ProcessStartInfo(Guide) { UseShellExecute = true })?.Dispose();
}

/// <summary>One box of the pairing code, or the dash between its groups.</summary>
public sealed record CodeChar(string Text, bool Dash);

/// <summary>
/// Pairing (the design canvas's "Pairing"): asks Race Control for a code with <see cref="PairingFlow"/>, shows it in
/// large characters with Copy and Open the pairing page, counts down until it expires, and waits for a steward's
/// approval. Local builds also take the server's address; a release build pairs only with its own.
/// </summary>
public sealed partial class PairingPageModel : ObservableObject
{
    private readonly PairingFlow _flow;
    private readonly Action<PairingOutcome> _done;
    private readonly Action _back;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _waiting;
    private DateTimeOffset? _expiresAt;

    public PairingPageModel(PairingFlow flow, Action<PairingOutcome> done, Action back)
    {
        _flow = flow;
        _done = done;
        _back = back;
        _serverText = flow.SuggestedServer();
        _clock.Tick += (_, _) => Tick();
    }

    /// <summary>A local build asks for the server; a release build has its own.</summary>
    public bool AsksForServer => _flow.FixedServer is null;

    public string ServerHost => _flow.FixedServer?.Host ?? "";

    [ObservableProperty]
    private string _serverText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    private IReadOnlyList<CodeChar> _code = [];

    public bool HasCode => Code.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    private string _pageUrl = "";

    /// <summary>The pairing page's address as people read it (without https://).</summary>
    public string PageText => PageUrl.Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');

    [ObservableProperty]
    private string _remaining = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _busy;

    public bool CanStart => !Busy;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _failed;

    private string _userCode = "";

    /// <summary>Starts at once when the screen opens: there is nothing to decide before the code.</summary>
    public void Open()
    {
        if (!Busy && !HasCode && !AsksForServer)
        {
            _ = StartAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Busy = true;
        Failed = false;
        Message = "Asking Race Control for a code...";
        _waiting = new CancellationTokenSource();
        var ui = Application.Current.Dispatcher;
        var outcome = await _flow.PairAsync(ServerText, code => ui.BeginInvoke(() => Show(code)), _waiting.Token);
        _clock.Stop();
        Busy = false;
        Code = [];
        Remaining = "";
        if (outcome.Paired)
        {
            Message = "";
            _done(outcome);
        }
        else
        {
            Failed = true;
            Message = outcome.Message;
        }
    }

    [RelayCommand]
    private void Back()
    {
        _waiting?.Cancel();
        _back();
    }

    [RelayCommand]
    private void CopyCode()
    {
        if (_userCode.Length > 0)
        {
            Clipboard.SetText(_userCode);
            Message = "Code copied.";
        }
    }

    [RelayCommand]
    private void OpenPage()
    {
        // Only a web address the server could mean (https, or http on this PC), never anything else.
        if (ServerAddress.TryParse(PageUrl, out _, out _) && Uri.TryCreate(PageUrl, UriKind.Absolute, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>For the render switch: a code as the server would give it.</summary>
    internal void Preview(PairingCodeMessage code) => Show(code);

    private void Show(PairingCodeMessage code)
    {
        _userCode = code.UserCode;
        Code = [.. code.UserCode.Select(c => new CodeChar(c.ToString(), c == '-'))];
        PageUrl = code.VerificationUrl;
        _expiresAt = code.ExpiresAt;
        Message = "";
        Tick();
        _clock.Start();
    }

    private void Tick()
    {
        if (_expiresAt is not { } until)
        {
            Remaining = "";
            return;
        }

        var left = until - DateTimeOffset.Now;
        Remaining = left <= TimeSpan.Zero ? "expired" : $"{(int)left.TotalMinutes}:{left.Seconds.ToString("00", CultureInfo.InvariantCulture)} left";
    }
}
