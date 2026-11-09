using System.Reflection;
using Microsoft.Extensions.Logging;
using PaceLaps.Agent.App.Render;
using PaceLaps.Agent.App.Shell;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.App;

/// <summary>
/// The operator's program (<c>pacelaps-agent.exe</c>): the tray icon and the app window. <c>--version</c> prints the
/// version for the packaging check; <c>--render DIR</c> (local builds) draws the screens to images.
/// </summary>
public static class Program
{
    /// <summary>The version with the short commit hash, within the contract's 40 characters.</summary>
    public static string AgentVersion =>
        Core.Agent.AgentVersion.Shorten(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The server a release build belongs to; null for a local build.</summary>
    public static Uri? ReleaseServer => ReleaseBuild.Server(typeof(Program).Assembly);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            // For the packaging check: shows nothing, prints the version to redirected output.
            Console.Out.WriteLine(AgentVersion);
            if (ReleaseServer is { } server)
            {
                Console.Out.WriteLine($"server {server}");
            }

            Console.Out.Flush();
            return 0;
        }

        if (args is ["--render", var dir])
        {
            // Development only: the screens as images. A release build has no use for it.
            return ReleaseServer is not null ? 2 : RenderScreens.Run(dir);
        }

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // The agent runs already; it was asked to show its window.
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        var recent = new RecentLog();
        using var loggers = AgentLogging.Create(recent);
        var logger = loggers.CreateLogger("App");
        logger.LogInformation("Start app {Version}", AgentVersion);
        var clock = new SystemClock();

        // Reading the sim is local (shared memory) and sends nothing: it runs from the start, so the status shows
        // whether iRacing is there. The server connection opens only on Activate.
        using var sim = new SimConnection(loggers.CreateLogger<SimConnection>(), clock);
        sim.Start(SimDataAdapter.TelemetryVars.Append("SessionTime"));
        var focus = new Win32WindowFocus();
        var prompt = new QuestionPrompt(app.Dispatcher);
        var parts = RuntimeParts.ForIRacing(sim, focus, new ScancodeKeyboard(focus.SimKeyboardLayout), prompt, clock, loggers);
        var config = new ConfigStore(ConfigStore.DefaultPath);
        var credentials = new CredentialStore(CredentialStore.DefaultPath);
        var runtime = new AgentRuntime(parts, config, credentials, AgentVersion, new RuntimeOverrides { Server = ReleaseServer });
        try
        {
            // The update notice: downloaded (release) builds only, unless the setting is off.
            using var updates = new UpdateWatch(AgentVersion, clock, () => ReleaseServer is not null && config.TryLoad().Config.CheckForUpdates,
                loggers.CreateLogger<UpdateWatch>());
            using var controller = new AgentController(runtime, recent, new PairingFlow(credentials, config, AgentVersion, clock, fixedServer: ReleaseServer),
                config, RunKeyAutoStart.ForThisProgram(), focus, updates, AgentVersion, loggers.CreateLogger<AgentController>());
            instance.ShowRequested += () => app.Dispatcher.BeginInvoke(controller.OpenWindow);
            controller.Start();
            return app.Run();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "The app failed");
            System.Windows.MessageBox.Show($"The agent stopped after an error.\n\nThe log folder has the details: {AgentLogging.LogDirectory}",
                OperatorWords.ProductName, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            // Sleep disconnects from Race Control before the program ends.
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            logger.LogInformation("Stop app");
        }
    }
}
