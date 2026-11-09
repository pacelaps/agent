using System.Windows;
using System.Windows.Controls;

namespace PaceLaps.Agent.App.Shell;

/// <summary>The window's content: title bar, rail, the screen. The window decides what minimize and close do.</summary>
public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
    }

    public event Action? MinimizeRequested;

    public event Action? CloseRequested;

    private void OnMinimize(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke();

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
