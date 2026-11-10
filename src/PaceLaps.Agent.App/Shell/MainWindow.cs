using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Shell;

/// <summary>
/// The app window: the shell in a frame of our own (WindowChrome: the title bar is ours, the edges still resize and
/// snap), with Windows 11's rounded corners. Closing it releases it (the WPF plan, W6); the agent runs on in the tray.
/// </summary>
public sealed class MainWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public MainWindow(ShellModel model)
    {
        Title = OperatorWords.ProductName;
        Width = 1000;
        Height = 660;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["Canvas"];
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/pacelaps-agent.ico"));
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var shell = new ShellView { DataContext = model };
        shell.MinimizeRequested += () => WindowState = WindowState.Minimized;
        shell.CloseRequested += Close;
        Content = shell;
        StateChanged += (_, _) =>
            // A maximized window reaches past the screen's edges by its frame: keep the content inside.
            BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        SourceInitialized += (_, _) => RoundCorners();
    }

    private void RoundCorners()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var preference = DwmwcpRound;
        // Windows 10 doesn't know the attribute and says so; nothing to do then.
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
