using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.App.Shell;

/// <summary>
/// A question in the app's look that comes in front of everything, iRacing too (the self-test's questions, the WPF
/// plan W5): the question, a line under it, and two answers. No is the default, so Enter doesn't say yes by accident.
/// </summary>
public sealed class QuestionWindow : Window
{
    public QuestionWindow(string question, string? detail = null, string yes = "Yes", string no = "No")
    {
        Title = OperatorWords.ProductName;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("Surface");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var text = new StackPanel { MaxWidth = 460 };
        text.Children.Add(new TextBlock
        {
            Text = question, FontSize = 18, FontWeight = FontWeights.ExtraBold, Foreground = Brush("Fg"), TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.Resources["Body"],
        });
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Text = detail, Style = (Style)Application.Current.Resources["BodyText"], FontSize = 13, Margin = new Thickness(0, 6, 0, 0) });
        }

        var noButton = new Button { Content = no, Style = (Style)Application.Current.Resources["Btn"], IsCancel = true, IsDefault = true, MinWidth = 90 };
        noButton.Click += (_, _) => DialogResult = false;
        var yesButton = new Button { Content = yes, Style = (Style)Application.Current.Resources["BtnPrimary"], Margin = new Thickness(10, 0, 0, 0), MinWidth = 90 };
        yesButton.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(noButton);
        buttons.Children.Add(yesButton);

        var layout = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        layout.Children.Add(new TextBlock { Text = "PACELAPS AGENT", Style = (Style)Application.Current.Resources["Eyebrow"], Foreground = Brush("BrandText"), Margin = new Thickness(0, 0, 0, 8) });
        layout.Children.Add(text);
        layout.Children.Add(buttons);
        var card = new Border { BorderBrush = Brush("BrandBorder"), BorderThickness = new Thickness(1), Child = layout };
        Content = card;

        // Sized from the content here, not with SizeToContent: with WindowChrome that sizes the window as if it still had
        // Windows' frame and title bar, which then leaves a black band below and beside the content.
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Math.Ceiling(card.DesiredSize.Width);
        Height = Math.Ceiling(card.DesiredSize.Height);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/pacelaps-agent.ico"));
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>
/// The self-test asks the operator through a <see cref="QuestionWindow"/> on the app's thread; <see cref="Asking"/> tells
/// the app's self-test screen what is being asked (null once answered).
/// </summary>
public sealed class QuestionPrompt(Dispatcher ui) : IOperatorPrompt
{
    public event Action<string?>? Asking;

    public Task<bool> ConfirmAsync(string question, CancellationToken ct) =>
        ui.InvokeAsync(() =>
        {
            Asking?.Invoke(question);
            try
            {
                return new QuestionWindow(question).ShowDialog() == true;
            }
            finally
            {
                Asking?.Invoke(null);
            }
        }).Task;
}
