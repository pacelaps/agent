using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Shell;

/// <summary>A chat macro slot's field (slot number and the text in it).</summary>
public sealed partial class MacroSlot(int number, string text) : ObservableObject
{
    public int Number { get; } = number;

    [ObservableProperty]
    private string _text = text;
}

/// <summary>
/// The settings screen (the design canvas's "Settings"): the fields of <see cref="SettingsModel"/> in four cards, checked
/// with the settings file's own parser on Save, so the screen names the same errors the file would. Changes apply at
/// the next Activate. The server's address shows only in local builds; a release build belongs to its server.
/// </summary>
public sealed partial class SettingsPageModel(ConfigStore config, IAutoStart autoStart, Uri? fixedServer) : ObservableObject
{
    public bool AsksForServer => fixedServer is null;

    [ObservableProperty]
    private string _server = "";

    [ObservableProperty]
    private string _league = "";

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _checkForUpdates;

    [ObservableProperty]
    private bool _typedWithoutFocus;

    [ObservableProperty]
    private bool _localFailsafeEnabled;

    [ObservableProperty]
    private string _localFailsafeDelay = "";

    [ObservableProperty]
    private bool _macrosEnabled;

    [ObservableProperty]
    private string _appIni = "";

    public IReadOnlyList<MacroSlot> MacroSlots { get; private set; } = [];

    /// <summary>Errors in the file or in the fields; empty when all is well.</summary>
    [ObservableProperty]
    private string _errors = "";

    [ObservableProperty]
    private string _message = "";

    /// <summary>Reads the settings file into the fields (on opening the screen and on Cancel).</summary>
    public void Load()
    {
        var (current, errors) = config.TryLoad();
        var model = SettingsModel.From(current, autoStart.Enabled);
        Server = model.Server;
        League = model.League;
        StartWithWindows = model.StartWithWindows;
        CheckForUpdates = model.CheckForUpdates;
        TypedWithoutFocus = model.TypedWithoutFocus;
        LocalFailsafeEnabled = model.LocalFailsafeEnabled;
        LocalFailsafeDelay = model.LocalFailsafeDelay;
        MacrosEnabled = model.MacrosEnabled;
        AppIni = model.AppIni;
        MacroSlots = [.. model.MacroSlots.Select((text, i) => new MacroSlot(i + 1, text))];
        OnPropertyChanged(nameof(MacroSlots));
        Errors = errors.Count > 0 ? "The settings file has errors; the screen shows the defaults for them:\n" + string.Join("\n", errors) : "";
        Message = "";
    }

    [RelayCommand]
    private void Save()
    {
        var model = new SettingsModel
        {
            Server = fixedServer?.ToString() ?? Server,
            League = League,
            MacrosEnabled = MacrosEnabled,
            LocalFailsafeEnabled = LocalFailsafeEnabled,
            LocalFailsafeDelay = LocalFailsafeDelay,
            AppIni = AppIni,
            TypedWithoutFocus = TypedWithoutFocus,
            CheckForUpdates = CheckForUpdates,
            StartWithWindows = StartWithWindows,
        };
        foreach (var slot in MacroSlots)
        {
            model.MacroSlots[slot.Number - 1] = slot.Text;
        }

        var (parsed, errors) = model.ToConfig();
        if (parsed is null)
        {
            Errors = "Not saved:\n" + string.Join("\n", errors);
            Message = "";
            return;
        }

        try
        {
            config.Save(parsed);
            if (autoStart.Enabled != model.StartWithWindows)
            {
                autoStart.Set(model.StartWithWindows);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Errors = $"Not saved: {e.Message}";
            return;
        }

        Errors = "";
        Message = "Saved. It applies at the next Activate.";
    }

    [RelayCommand]
    private void Cancel() => Load();

    [RelayCommand]
    private void OpenFile()
    {
        if (!File.Exists(config.Path))
        {
            config.Save(config.TryLoad().Config);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{config.Path}\"") { UseShellExecute = true })?.Dispose();
    }

    [RelayCommand]
    private void BrowseAppIni()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "app.ini|app.ini|INI files (*.ini)|*.ini", Title = "iRacing's app.ini", CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            AppIni = dialog.FileName;
        }
    }
}
