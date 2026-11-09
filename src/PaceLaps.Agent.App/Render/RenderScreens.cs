using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaceLaps.Agent.App.Shell;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

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
            if (name == "selftest-running")
            {
                model.SelfTest.Asking = "Did iRacing show the reply to !help in the chat?";
            }
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                Save(new ShellView { DataContext = model }, 1000, 660, scale, Path.Combine(dir, $"{name}{(scale == 1 ? "" : "@150")}.png"));
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), "pacelaps-render");
        Directory.CreateDirectory(temp);
        var flow = new PairingFlow(new CredentialStore(Path.Combine(temp, "credentials.bin")), new ConfigStore(Path.Combine(temp, "config.json")), "0.13.0",
            new PaceLaps.Agent.Core.Time.SystemClock(), fixedServer: Server);
        var unpaired = new AgentStatus { State = AgentState.Sleeping, Server = Server, SimRunning = true };
        foreach (var (name, screen) in new[] { ("welcome", Screen.Welcome), ("pairing", Screen.Pairing) })
        {
            var model = new ShellModel("0.13.0", _ => Task.CompletedTask);
            model.Welcome = new WelcomePageModel(new NoAutoStart(), () => { });
            model.Pairing = new PairingPageModel(flow, _ => { }, () => { });
            model.Pairing.Preview(new PairingCodeMessage
            {
                PairingId = "p1", UserCode = "K7Q-4MD", VerificationUrl = "https://racecontrol.pacelaps.com/pair", ExpiresAt = DateTimeOffset.Now.AddMinutes(9.7), IntervalSec = 3,
            });
            model.Update(unpaired);
            model.Screen = screen;
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
            RecentCommands = Commands(),
        };

        yield return ("status-executor", active, Screen.Status);
        yield return ("status-standby", active with { IsExecutor = false }, Screen.Status);
        yield return ("status-ready", active with { State = AgentState.Ready, SessionName = null, IsExecutor = false }, Screen.Status);
        yield return ("status-sleeping", sleeping with { LastSelfTest = Passed(), LastSelfTestAt = DateTimeOffset.Now.AddHours(-2) }, Screen.Status);
        yield return ("status-offline", active with { ServerOnline = false, EventsBuffered = 7 }, Screen.Status);
        yield return ("status-failsafe", active with { ServerOnline = false, EventsBuffered = 7, LocalFailsafeSince = DateTimeOffset.Now.AddSeconds(-95), RecentCommands = FailsafeCommands() }, Screen.Status);
        yield return ("status-not-paired", sleeping with { AgentId = null, SimInSession = false, CarCount = 0 }, Screen.Status);
        var progress = Passed().Steps.Take(4).ToList();
        yield return ("selftest-running", sleeping with { State = AgentState.SelfTest, SelfTestProgress = progress }, Screen.SelfTest);
        var failed = new SelfTestReport(false, [.. progress, new SelfTestStep(5, "typed channel", false, "no reply to !help (admin rights?)")], Channels, null);
        yield return ("selftest-failed", sleeping with { LastSelfTest = failed, LastSelfTestAt = DateTimeOffset.Now }, Screen.SelfTest);
        yield return ("selftest-passed", active, Screen.SelfTest);
        yield return ("settings-placeholder", active, Screen.Settings);
    }

    private sealed class NoAutoStart : PaceLaps.Agent.Hosting.Tray.IAutoStart
    {
        public bool Enabled => false;

        public void Set(bool enabled)
        {
        }
    }

    private static List<FinishedCommand> Commands()
    {
        var now = DateTimeOffset.Now;
        return
        [
            new("c4", "yellow", "!yellow Full course caution", CommandOutcome.Verified, "via typed; caution flag", now.AddSeconds(-14), TimeSpan.FromMilliseconds(166), false),
            new("c3", "pit_close", "!pitclose", CommandOutcome.Sent, "via typed", now.AddMinutes(-3), TimeSpan.FromMilliseconds(161), false),
            new("c2", "black_flag", "!black #42 D", CommandOutcome.Verified, "via typed; black flag", now.AddMinutes(-4), TimeSpan.FromMilliseconds(172), false),
            new("c1", "announce", "Pit lane closes in 5 minutes", CommandOutcome.Sent, "via typed", now.AddMinutes(-8), TimeSpan.FromMilliseconds(158), false),
        ];
    }

    private static List<FinishedCommand> FailsafeCommands()
    {
        var now = DateTimeOffset.Now;
        return
        [
            new("local_3", "pace_laps_add", "!pacelaps +1", CommandOutcome.Sent, "via typed", now.AddSeconds(-20), TimeSpan.FromMilliseconds(163), true),
            new("local_2", "yellow", "!yellow Full course yellow, race control lost connection", CommandOutcome.Verified, "via typed; caution flag", now.AddSeconds(-94), TimeSpan.FromMilliseconds(170), true),
            new("local_1", "restart", "!restart single", CommandOutcome.Sent, "via typed", now.AddSeconds(-95), TimeSpan.FromMilliseconds(159), true),
            .. Commands().Skip(1),
        ];
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
