using System.Drawing.Imaging;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Updates;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;
using Serilog;

namespace PaceLaps.Agent.Tray;

/// <summary>
/// Development only (`pacelaps-agent.exe --render DIR`, local builds): draws the agent's windows in fixed example states
/// and the tray icons to PNG files, to check a layout without clicking through the states, and for the guide's
/// screenshots. The windows are drawn off screen at this PC's scaling; nothing talks to iRacing or a server.
/// </summary>
internal static class RenderWindows
{
    private static readonly Uri Server = new("https://racecontrol.pacelaps.com/");
    private static readonly ChannelSet Channels = new(ChannelReadiness.Disabled, ChannelReadiness.Ready, ChannelReadiness.Ready);
    private static readonly StatusActions None = new(() => { }, () => { }, () => Task.CompletedTask, () => { }, () => { });

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var recent = SampleLog();
        foreach (var (name, status, expand) in Samples())
        {
            var form = new StatusForm(None);
            Draw(form, Path.Combine(dir, $"status-{name}.png"), () =>
            {
                form.Show(status, recent);
                if (expand)
                {
                    form.OpenDetailsAndLog();
                }
            });
            form.CloseForExit();
        }

        using (var about = new AboutForm("0.13.0", Server))
        {
            Draw(about, Path.Combine(dir, "about.png"));
        }

        using (var update = new UpdateForm(new UpdateOffer("0.13.0", ["A new status window that says what to do next.", "The PaceLaps icon in the tray."]), "0.12.0"))
        {
            Draw(update, Path.Combine(dir, "update.png"));
        }

        DrawTrayIcons(Path.Combine(dir, "tray-icons.png"));
        Console.Out.WriteLine($"Rendered into {Path.GetFullPath(dir)}");
        Console.Out.Flush();
        return 0;
    }

    private static IEnumerable<(string Name, AgentStatus Status, bool Expand)> Samples()
    {
        var sleeping = new AgentStatus { State = AgentState.Sleeping, AgentId = "agt_y8nqfhpnod6f", Server = Server, SimRunning = true, SimInSession = true };
        var active = sleeping with
        {
            State = AgentState.Active, ServerOnline = true, Realtime = true, IsExecutor = true, Epoch = 3, SessionName = "Round 4 Race", Channels = Channels,
            ClockOffset = TimeSpan.FromMilliseconds(14), LocalFailsafe = new LocalFailsafeSettings { Enabled = true, DelaySeconds = 30 },
            LocalFailsafeHeld = "Local failsafe v2", LastSelfTest = Passed(), LastSelfTestAt = DateTimeOffset.Now.AddMinutes(-12),
        };
        var failed = new SelfTestReport(false,
            [new SelfTestStep(1, "sim and telemetry", true, "build 2026.11.02.01"), new SelfTestStep(2, "session identity", true, "league 7700, sub-session 89997704, session 2 Race"),
             new SelfTestStep(3, "server and clock", true, "5/5 pings, offset 14 ms, round trip 38 ms"), new SelfTestStep(4, "cars", true, "22 cars, 22 in the world"),
             new SelfTestStep(5, "typed channel", false, "no reply to !help (admin rights?)")], Channels, null);

        yield return ("not-paired", sleeping with { AgentId = null, SimInSession = false }, false);
        yield return ("sleeping", sleeping, false);
        yield return ("self-test-failed", sleeping with { Problem = "The self-test failed: Typing into iRacing.", LastSelfTest = failed, LastSelfTestAt = DateTimeOffset.Now }, false);
        yield return ("ready", active with { State = AgentState.Ready, SessionName = null, IsExecutor = false }, false);
        yield return ("executor", active, false);
        yield return ("standby", active with { IsExecutor = false }, false);
        yield return ("offline", active with { ServerOnline = false, EventsBuffered = 7 }, false);
        yield return ("failsafe", active with { ServerOnline = false, LocalFailsafeSince = DateTimeOffset.Now.AddSeconds(-95) }, false);
        yield return ("details", active, true);
    }

    private static SelfTestReport Passed() => new(true,
        [new SelfTestStep(1, "sim and telemetry", true, "build 2026.11.02.01"), new SelfTestStep(2, "session identity", true, "league 7700, sub-session 89997704, session 2 Race"),
         new SelfTestStep(3, "server and clock", true, "5/5 pings, offset 14 ms, round trip 38 ms"), new SelfTestStep(4, "cars", true, "22 cars, 22 in the world"),
         new SelfTestStep(5, "typed channel", true, "!help in 612 ms, focus restored True"), new SelfTestStep(6, "macro channel", true, "disabled", SelfTestStepResult.Skipped)],
        Channels, null);

    private static RecentLog SampleLog()
    {
        var recent = new RecentLog();
        using var log = new LoggerConfiguration().WriteTo.Sink(recent).CreateLogger();
        log.Information("Start tray app 0.13.0");
        log.Information("iRacing connected");
        log.Information("State Sleeping -> SelfTest: activated by the operator");
        log.Information("State SelfTest -> Ready: self-test passed");
        log.Information("State Ready -> Active: joined the session");
        log.Information("Command yellow sent via typed in 166 ms");
        return recent;
    }

    private static void Draw(Form form, string path, Action? setup = null)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.ShowInTaskbar = false;
        setup?.Invoke();
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, ImageFormat.Png);
        form.Hide();
    }

    /// <summary>The five tray icons at this PC's size and four times larger, on a light and a dark strip.</summary>
    private static void DrawTrayIcons(string path)
    {
        using var icons = new TrayIcons();
        var kinds = Enum.GetValues<TrayIcon>();
        var size = SystemInformation.SmallIconSize.Width;
        var big = size * 4;
        using var bitmap = new Bitmap(kinds.Length * (big + 16) + 16, (big + size + 24) * 2);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        foreach (var (row, background) in new[] { (0, Color.FromArgb(0xF3, 0xF3, 0xF3)), (1, Color.FromArgb(0x20, 0x20, 0x20)) })
        {
            var top = row * (big + size + 24);
            using var fill = new SolidBrush(background);
            g.FillRectangle(fill, 0, top, bitmap.Width, big + size + 24);
            for (var i = 0; i < kinds.Length; i++)
            {
                using var icon = icons[kinds[i]].ToBitmap();
                var x = 16 + i * (big + 16);
                g.DrawImage(icon, x, top + 8, size, size);
                g.DrawImage(icon, x, top + size + 16, big, big);
            }
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
