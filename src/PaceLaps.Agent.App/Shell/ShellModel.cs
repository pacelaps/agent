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
}

/// <summary>A screen that isn't built yet (the WPF plan's later steps).</summary>
public sealed record PlaceholderPage(string Title, string Text);

/// <summary>
/// The app window's model: the screen shown, the pairing card at the bottom of the rail, and the screens' models. The
/// controller hands it every new status; nothing here talks to the agent directly.
/// </summary>
public sealed partial class ShellModel : ObservableObject
{
    private readonly PlaceholderPage _settings = new("Settings", "The settings screen comes in the next steps.");

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
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private Screen _screen = Screen.Status;

    [ObservableProperty]
    private bool _paired;

    [ObservableProperty]
    private string _pairedHost = "";

    [ObservableProperty]
    private string _agentId = "";

    public object CurrentPage => Screen switch
    {
        Screen.SelfTest => SelfTest,
        Screen.Settings => _settings,
        _ => Status,
    };

    [RelayCommand]
    private void Navigate(Screen screen) => Screen = screen;

    public void Update(AgentStatus status)
    {
        Status.View = StatusView.From(status);
        SelfTest.View = SelfTestView.From(status);
        Paired = status.Paired;
        PairedHost = status.Server?.Host ?? "";
        AgentId = status.AgentId ?? "";
    }
}
