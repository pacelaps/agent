using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Shell;

/// <summary>
/// The self-test screen (the design canvas's "Self-test"): everything from <see cref="SelfTestView"/>, plus the question
/// the self-test is asking right now, which the operator answers in the question window in front of iRacing.
/// </summary>
public sealed partial class SelfTestPageModel(Action run) : ObservableObject
{
    [ObservableProperty]
    private SelfTestView? _view;

    [ObservableProperty]
    private string? _asking;

    [RelayCommand]
    private void Run() => run();
}
