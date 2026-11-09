using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaceLaps.Agent.App.Shell;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Hosting;

namespace PaceLaps.Agent.App.Render;

/// <summary>
/// Development only (<c>pacelaps-agent.exe --render DIR</c>, local builds): draws the app's screens in fixed example
/// states to PNG files at 100 % and 150 % scaling, to compare them with the design canvas and for the guide's
/// screenshots. Nothing talks to iRacing or a server.
/// </summary>
public static class RenderScreens
{
    private static readonly Uri Server = new("https://racecontrol.pacelaps.com/");
    private static readonly ChannelSet Channels = new(ChannelReadiness.Disabled, ChannelReadiness.Ready, ChannelReadiness.Ready);

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new App();
        app.InitializeComponent();
        foreach (var (name, status, screen) in Samples())
        {
            var model = new ShellModel("0.13.0", _ => Task.CompletedTask) { Screen = screen };
            model.Update(status);
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                Save(new ShellView { DataContext = model }, 1000, 660, scale, Path.Combine(dir, $"{name}{(scale == 1 ? "" : "@150")}.png"));
            }
        }

        var question = new QuestionWindow("Did iRacing show the reply to !help in the chat?", "It lists the admin commands. If nothing appeared, you may not be an admin in this session.");
        var content = (FrameworkElement)question.Content;
        question.Content = null;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Save(content, content.DesiredSize.Width, content.DesiredSize.Height, 1, Path.Combine(dir, "question.png"), (Brush)app.Resources["Surface"]);

        Console.Out.WriteLine($"Rendered into {Path.GetFullPath(dir)}");
        Console.Out.Flush();
        return 0;
    }

    private static IEnumerable<(string Name, AgentStatus Status, Screen Screen)> Samples()
    {
        var sleeping = new AgentStatus { State = AgentState.Sleeping, AgentId = "agt_y8nqfhpnod6f", Server = Server, SimRunning = true, SimInSession = true, CarCount = 22 };
        var active = sleeping with
        {
            State = AgentState.Active, ServerOnline = true, Realtime = true, IsExecutor = true, Epoch = 3, SessionName = "Round 4 Race", Channels = Channels,
            TrackName = "Okayama International Circuit", ClockOffset = TimeSpan.FromMilliseconds(14), LocalFailsafe = new LocalFailsafeSettings { Enabled = true, DelaySeconds = 30 },
            LocalFailsafeHeld = "Local failsafe v2", LastSelfTest = Passed(), LastSelfTestAt = DateTimeOffset.Now.AddMinutes(-12),
        };

        yield return ("status-executor", active, Screen.Status);
        yield return ("status-standby", active with { IsExecutor = false }, Screen.Status);
        yield return ("status-ready", active with { State = AgentState.Ready, SessionName = null, IsExecutor = false }, Screen.Status);
        yield return ("status-sleeping", sleeping with { LastSelfTest = Passed(), LastSelfTestAt = DateTimeOffset.Now.AddHours(-2) }, Screen.Status);
        yield return ("status-offline", active with { ServerOnline = false, EventsBuffered = 7 }, Screen.Status);
        yield return ("status-failsafe", active with { ServerOnline = false, EventsBuffered = 7, LocalFailsafeSince = DateTimeOffset.Now.AddSeconds(-95) }, Screen.Status);
        yield return ("status-not-paired", sleeping with { AgentId = null, SimInSession = false, CarCount = 0 }, Screen.Status);
        yield return ("settings-placeholder", active, Screen.Settings);
    }

    private static SelfTestReport Passed() => new(true,
        [new SelfTestStep(1, "sim and telemetry", true, "build 2026.11.02.01"), new SelfTestStep(2, "session identity", true, "league 7700, sub-session 89997704, session 2 Race"),
         new SelfTestStep(3, "server and clock", true, "5/5 pings, offset 14 ms, round trip 38 ms"), new SelfTestStep(4, "cars", true, "22 cars, 22 in the world"),
         new SelfTestStep(5, "typed channel", true, "!help in 612 ms, focus restored True"), new SelfTestStep(6, "macro channel", true, "disabled", SelfTestStepResult.Skipped)],
        Channels, null);

    private static void Save(FrameworkElement element, double width, double height, double scale, string path, Brush? background = null)
    {
        FrameworkElement root = background is null ? element : new System.Windows.Controls.Border { Background = background, Child = element };
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }
}
