using System.Reflection;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.IRacing.Win32;
using Microsoft.Extensions.Logging;

namespace PaceLaps.Agent.Tray;

internal static class Program
{
    /// <summary>The version with the short commit hash, within the contract's 40 characters.</summary>
    public static string AgentVersion =>
        Core.Agent.AgentVersion.Shorten(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            // For the packaging check: shows nothing, prints the version to redirected output.
            Console.Out.WriteLine(AgentVersion);
            if (ReleaseBuild.Server(typeof(Program).Assembly) is { } server)
            {
                Console.Out.WriteLine($"server {server}");
            }

            Console.Out.Flush();
            return 0;
        }

        if (args is ["--render", var renderDir])
        {
            // Development only: the windows as images (RenderWindows). A release build has no use for it.
            if (ReleaseBuild.Server(typeof(Program).Assembly) is not null)
            {
                return 2;
            }

            ApplicationConfiguration.Initialize();
            return RenderWindows.Run(renderDir);
        }

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            // The agent runs already; it was asked to show its status window.
            return 0;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        var recent = new RecentLog();
        using var loggers = AgentLogging.Create(recent);
        var logger = loggers.CreateLogger("Tray");
        logger.LogInformation("Start tray app {Version}", AgentVersion);
        var clock = new SystemClock();

        // Reading the sim is local (shared memory) and sends nothing: it runs from the start, so the status shows
        // whether iRacing is there. The server connection opens only on Activate.
        using var sim = new SimConnection(loggers.CreateLogger<SimConnection>(), clock);
        sim.Start(SimDataAdapter.TelemetryVars.Append("SessionTime"));
        var focus = new Win32WindowFocus();
        var prompt = new DialogPrompt(SynchronizationContext.Current!);
        var parts = RuntimeParts.ForIRacing(sim, focus, new ScancodeKeyboard(focus.SimKeyboardLayout), prompt, clock, loggers);
        var config = new ConfigStore(ConfigStore.DefaultPath);
        var credentials = new CredentialStore(CredentialStore.DefaultPath);
        // A release build belongs to its server; a local build takes the settings file's.
        var releaseServer = ReleaseBuild.Server(typeof(Program).Assembly);
        var runtime = new AgentRuntime(parts, config, credentials, AgentVersion, new RuntimeOverrides { Server = releaseServer });
        try
        {
            // The update notice: downloaded (release) builds only, unless the setting is off.
            var updates = new UpdateWatch(AgentVersion, clock, () => releaseServer is not null && config.TryLoad().Config.CheckForUpdates,
                loggers.CreateLogger<UpdateWatch>());
            using var context = new TrayContext(runtime, recent, new PairingFlow(credentials, config, AgentVersion, clock, fixedServer: releaseServer), config,
                RunKeyAutoStart.ForThisProgram(), focus, updates, AgentVersion, loggers.CreateLogger<TrayContext>());
            instance.ShowRequested += context.ShowStatusFromAnotherStart;
            Application.Run(context);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "The tray app failed");
            MessageBox.Show($"The agent stopped after an error.\n\nThe log folder has the details: {AgentLogging.LogDirectory}", OperatorWords.ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            // Sleep disconnects from Race Control before the program ends.
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            logger.LogInformation("Stop tray app");
        }

        return 0;
    }
}
