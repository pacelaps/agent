using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Tray;

/// <summary>What the tray menu's items do.</summary>
internal sealed record TrayActions(Action Open, Action Activate, Func<Task> Sleep, Action Settings, Action Update, Action OpenLogFolder, Func<Task> Exit);

/// <summary>
/// The tray menu in the app's look (the design canvas's "Tray menu"): the state on top, then Open, Activate or Sleep,
/// the update when there is one, Settings, the log folder and Exit.
/// </summary>
internal sealed class TrayMenu
{
    private readonly TextBlock _title = new() { FontSize = 20, LineHeight = 20 };
    private readonly TextBlock _line = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly MenuItem _activate;
    private readonly MenuItem _sleep;
    private readonly MenuItem _update;
    private readonly Separator _updateSeparator = Line();

    public TrayMenu(TrayActions actions)
    {
        _title.FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"];
        _title.FontStyle = FontStyles.Italic;
        _title.Foreground = Brush("Fg");
        _line.Foreground = Brush("FgMuted");
        _activate = Item("Activate", actions.Activate);
        _sleep = Item("Sleep", () => _ = actions.Sleep());
        _update = Item("", actions.Update);
        _update.Visibility = Visibility.Collapsed;

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, 8, 0, 8) };
        head.Children.Add(_dot);
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        text.Children.Add(_title);
        text.Children.Add(_line);
        head.Children.Add(text);

        _updateSeparator.Visibility = Visibility.Collapsed;
        var state = new MenuItem { Header = head, IsEnabled = false };
        var open = Item("Open PaceLaps Agent", actions.Open);
        open.FontWeight = FontWeights.Bold;
        Menu = new ContextMenu { Style = (Style)Application.Current.Resources["TrayMenu"] };
        foreach (var element in new FrameworkElement[]
                 {
                     state, Line(), open, _activate, _sleep, _updateSeparator, _update, Line(), Item("Settings", actions.Settings),
                     Item("Open the log folder", actions.OpenLogFolder), Line(), Item("Exit", () => _ = actions.Exit()),
                 })
        {
            Menu.Items.Add(element);
        }
    }

    public ContextMenu Menu { get; }

    public void Show(TrayView view, StatusView status, string? update)
    {
        _title.Text = status.Title.ToUpperInvariant();
        _line.Text = status.SideName is { } name && status.Role is { } role ? $"{name} · {role.ToLowerInvariant()}" : status.Eyebrow;
        _dot.Fill = view.Icon switch
        {
            TrayIcon.Active => Brush("Green"),
            TrayIcon.Ready => Brush("Yellow"),
            TrayIcon.Busy => Brush("Brand"),
            TrayIcon.Problem => Brush("Red"),
            _ => Brush("GreyDot"),
        };
        _activate.Visibility = view.CanActivate || !view.CanSleep ? Visibility.Visible : Visibility.Collapsed;
        _activate.IsEnabled = view.CanActivate;
        _sleep.Visibility = view.CanSleep ? Visibility.Visible : Visibility.Collapsed;
        _update.Header = update ?? "";
        _update.Visibility = _updateSeparator.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Separator Line() => new() { Style = (Style)Application.Current.Resources["MenuLine"] };

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
