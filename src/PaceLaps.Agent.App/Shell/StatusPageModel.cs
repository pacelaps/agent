using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Shell;

/// <summary>The status screen (the design canvas's "Status"): everything comes from <see cref="StatusView"/>.</summary>
public sealed partial class StatusPageModel(Func<StatusAction, Task> act) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private StatusView? _view;

    public string ActionText => View is null ? "" : StatusView.ActionText(View.Action).TrimEnd('.');

    [RelayCommand]
    private Task ActAsync() => View is { Action: not StatusAction.None } view ? act(view.Action) : Task.CompletedTask;
}
