using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace PaceLaps.Agent.App.Tray;

/// <summary>A notification's colour: green for good news, red for trouble, blue for the rest (an update).</summary>
public enum ToastTone
{
    Good,
    Trouble,
    Info,
}

/// <summary>
/// The app's own notifications (the WPF plan, W4): small windows in the app's look at the bottom right of the screen
/// iRacing is on, shown without activation (never the foreground window, so typing in the sim is never interrupted),
/// gone after a few seconds, stacked upwards when several come. A click opens the app. An exclusive fullscreen iRacing
/// hides them, as it hides Windows' own.
/// </summary>
public sealed class Toasts(Func<IntPtr> simWindow, Action open)
{
    public static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(6);
    private const int MaxShown = 3;
    private readonly List<ToastWindow> _shown = [];

    public void Show(string title, string text, ToastTone tone)
    {
        while (_shown.Count >= MaxShown)
        {
            _shown[0].Close();
        }

        var toast = new ToastWindow(title, text, tone);
        toast.Closed += (_, _) =>
        {
            _shown.Remove(toast);
            Arrange();
        };
        toast.MouseLeftButtonUp += (_, _) =>
        {
            toast.Close();
            open();
        };
        _shown.Add(toast);
        toast.Show();
        Arrange();
        var timer = new DispatcherTimer { Interval = ShowFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            toast.Close();
        };
        timer.Start();
    }

    /// <summary>Newest at the bottom, the others above it, at the bottom right of the sim's screen (else the main screen).</summary>
    private void Arrange()
    {
        var area = WorkArea();
        var bottom = area.Bottom - 12;
        for (var i = _shown.Count - 1; i >= 0; i--)
        {
            var toast = _shown[i];
            toast.UpdateLayout();
            var height = toast.ActualHeight > 0 ? toast.ActualHeight : 96;
            toast.Left = area.Right - toast.Width - 12;
            toast.Top = bottom - height;
            bottom -= height + 8;
        }
    }

    private Rect WorkArea()
    {
        var sim = simWindow();
        var monitor = sim == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(sim, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        // The monitor's work area is in pixels; the windows are placed in device-independent units of that screen.
        var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
        return new Rect(info.Work.Left / scale, info.Work.Top / scale, (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
    }

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rectangle Monitor;
        public Rectangle Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

/// <summary>One notification: the tone's strip, the mark, a small line, the title and the text.</summary>
public sealed class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    public ToastWindow(string title, string text, ToastTone tone)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        Cursor = Cursors.Hand;
        Content = Card(title, text, tone);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        };
    }

    /// <summary>The notification's content (also drawn by the render switch).</summary>
    public static Border Card(string title, string text, ToastTone tone)
    {
        var strip = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(11, 11, 0, 0),
            Background = Brush(tone switch { ToastTone.Good => "Green", ToastTone.Trouble => "Red", _ => "Brand" }),
        };
        var mark = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo-256.png")),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
        var words = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        words.Children.Add(new TextBlock
        {
            Text = (string)new Themes.Upper().Convert("PaceLaps Agent · now", typeof(string), "spaced", System.Globalization.CultureInfo.CurrentCulture),
            Style = (Style)Application.Current.Resources["Eyebrow"],
            FontSize = 10,
        });
        words.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.ExtraBold, Foreground = Brush("Fg"), Margin = new Thickness(0, 4, 0, 0),
            FontFamily = (FontFamily)Application.Current.Resources["Body"], TextWrapping = TextWrapping.Wrap,
        });
        words.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, Foreground = Brush("FgMuted"), Margin = new Thickness(0, 4, 0, 0), LineHeight = 19,
            FontFamily = (FontFamily)Application.Current.Resources["Body"], TextWrapping = TextWrapping.Wrap,
        });
        var body = new DockPanel { Margin = new Thickness(16, 14, 16, 14) };
        DockPanel.SetDock(mark, Dock.Left);
        body.Children.Add(mark);
        body.Children.Add(words);
        var layout = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        layout.Children.Add(strip);
        layout.Children.Add(body);
        return new Border
        {
            Background = Brush("Surface"),
            BorderBrush = Brush("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = layout,
            Margin = new Thickness(0, 0, 0, 0),
        };
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLong(IntPtr window, int index, nint value);
}
