using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Shell;

/// <summary>The screens the rail switches between.</summary>
public enum Screen
{
    Status,
    SelfTest,
    Settings,
    Welcome,
    Pairing,
}

/// <summary>
/// The app window's model: the screen shown, the pairing card at the bottom of the rail, and the screens' models. The
/// controller hands it every new status; nothing here talks to the agent directly.
/// </summary>
public sealed partial class ShellModel : ObservableObject
{
    public ShellModel(string version, Func<StatusAction, Task> act)
    {
        Version = version;
        Status = new StatusPageModel(act);
        SelfTest = new SelfTestPageModel(() => _ = act(StatusAction.Activate));
    }

    public string Version { get; }

    public StatusPageModel Status { get; }

    public SelfTestPageModel SelfTest { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(ShowRail))]
    private Screen _screen = Screen.Status;

    /// <summary>The first start and pairing fill the window; the other screens have the rail.</summary>
    public bool ShowRail => Screen is not (Screen.Welcome or Screen.Pairing);

    /// <summary>Set by the controller, which has the pairing and the start with Windows.</summary>
    public WelcomePageModel? Welcome { get; set; }

    public PairingPageModel? Pairing { get; set; }

    public SettingsPageModel? Settings { get; set; }

    [ObservableProperty]
    private bool _paired;

    [ObservableProperty]
    private string _pairedHost = "";

    [ObservableProperty]
    private string _agentId = "";

    public object CurrentPage => Screen switch
    {
        Screen.SelfTest => SelfTest,
        Screen.Welcome => (object?)Welcome ?? Status,
        Screen.Pairing => (object?)Pairing ?? Status,
        Screen.Settings => (object?)Settings ?? Status,
        _ => Status,
    };

    [RelayCommand]
    private void Navigate(Screen screen) => Screen = screen;

    partial void OnScreenChanged(Screen value)
    {
        if (value == Screen.Pairing)
        {
            Pairing?.Open();
        }
        else if (value == Screen.Settings)
        {
            Settings?.Load();
        }
    }

    public void Update(AgentStatus status)
    {
        Status.View = StatusView.From(status);
        SelfTest.View = SelfTestView.From(status);
        Paired = status.Paired;
        PairedHost = status.Server?.Host ?? "";
        AgentId = status.AgentId ?? "";
    }
}
