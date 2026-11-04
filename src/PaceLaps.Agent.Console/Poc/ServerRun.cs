using System.Globalization;
using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.IRacing.Sdk;
using Microsoft.Extensions.Logging;

namespace PaceLaps.Agent.ConsoleHost.Poc;

/// <summary>
/// <c>run --server URL</c>: the agent with the web service, on the same <see cref="AgentRuntime"/> as the tray app
/// (Sleeping: no connection; Activate opens it). Every message goes to the run log (<c>integration-run-*.jsonl</c>).
/// </summary>
internal static class ServerRun
{
    private const string Help = """
        Agent: activate | sleep | status | offline SECONDS (simulate a lost connection) | quit
        Commands come from the race control page.
        """;

    /// <param name="macros">The macro slots from --macros; null for the settings file's.</param>
    public static async Task<int> RunAsync(SpikeEnvironment env, Uri serverUrl, MacroTable? macros)
    {
        var credentials = new CredentialStore(CredentialStore.DefaultPath).Load();
        if (credentials is null || !ServerAddress.Same(credentials.Server, serverUrl))
        {
            throw new CliException($"This PC is not paired with {serverUrl}. Run: pair --server {serverUrl}");
        }

        if (!await env.ConnectAsync(SimDataAdapter.TelemetryVars) || !env.PreflightTyping())
        {
            return 1;
        }

        var stamp = $"{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z";
        var outbox = Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "poc-logs", $"integration-run-{stamp}.jsonl");
        using var runLog = new RunLog(outbox, env.Clock);
        RecordingLink? recording = null;
        var chatCheck = !env.Args.Has("no-chat-check");
        var parts = RuntimeParts.ForIRacing(env.Sim, env.Focus, env.Keyboard, new ConsolePrompt(), env.Clock, env.LoggerFactory,
            env.TypedChatOptionsFromArgs(), chatCheck, c => new HttpServerConnection(new HttpServerLink(new HttpServerLinkOptions
            {
                BaseUrl = c.Server,
                AgentId = c.AgentId,
                Token = c.Token,
                RealtimeKey = env.Args.Get("realtime-key"),
            }, env.Clock, env.LoggerFactory.CreateLogger<HttpServerLink>()), link => recording = new RecordingLink(link, runLog)));
        var overrides = new RuntimeOverrides
        {
            Server = serverUrl,
            League = env.Args.Has("league") ? env.Args.GetInt("league", 0, 1, int.MaxValue) : null,
            Macros = macros,
            AppIni = env.Args.Get("app-ini"),
            HarmlessCommand = env.Args.Get("harmless"),
            Replay = !env.Args.Has("no-replay"),
            Limits = RunCommand.Limits(env.Args),
            TypedWithoutFocus = env.TypedWithoutFocusFromArgs(),
        };
        await using var runtime = new AgentRuntime(parts, new ConfigStore(ConfigStore.DefaultPath), new CredentialStore(CredentialStore.DefaultPath),
            SpikeEnvironment.AgentVersion, overrides);
        runtime.StateChanged += (from, to, reason) =>
        {
            runLog.Note("state", new { from, to, reason });
            Console.WriteLine($"[agent] {from} -> {to}: {reason}");
        };
        runLog.Note("start", new { credentials.AgentId, SpikeEnvironment.AgentVersion, overrides, server = serverUrl.ToString(), args = Environment.GetCommandLineArgs().Skip(1) });

        if (chatCheck)
        {
            Prompt.Warn("The agent reads the chat box through the clipboard after every typed line. Clipboard text is restored; images and files on it are not.");
        }

        Prompt.Heading($"Agent {credentials.AgentId} for {serverUrl}");
        Console.WriteLine($"Run log: {outbox}");

        // Started from inside a packaged app (an MSIX app), Windows redirects this folder.
        Console.WriteLine($"Diagnostic log: {AgentLogging.LogDirectory}");
        Console.WriteLine(Help);
        Console.WriteLine("The agent is Sleeping (no connection to the server). Type 'activate' to run the self-test.");
        try
        {
            while (true)
            {
                Console.Write("> ");
                if (Console.ReadLine() is not { } input)
                {
                    break;
                }

                var line = input.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                runLog.Note("input", new { line });
                switch (line.Split(' ', 2)[0].ToLowerInvariant())
                {
                    case "quit" or "exit":
                        return 0;
                    case "help":
                        Console.WriteLine(Help);
                        break;
                    case "activate":
                        var report = await runtime.ActivateAsync(env.Cancellation);
                        if (report is null)
                        {
                            Console.WriteLine(runtime.Problem is { } problem ? $"  {problem}" : $"  Cannot activate in state {runtime.State}.");
                            break;
                        }

                        foreach (var step in AgentStatus.DescribeSteps(report))
                        {
                            Console.WriteLine("  " + step);
                        }

                        runLog.Note("self_test", report);
                        break;
                    case "sleep":
                        await runtime.SleepAsync();
                        break;
                    case "status":
                        foreach (var l in runtime.Status().Describe())
                        {
                            Console.WriteLine("  " + l);
                        }

                        Console.WriteLine($"  Snapshots sent: {recording?.SnapshotCount ?? 0}");
                        break;
                    case "offline":
                        if (runtime.Connection is not HttpServerConnection connection)
                        {
                            Console.WriteLine("  Not connected: activate first.");
                            break;
                        }

                        var seconds = int.TryParse(line.Split(' ', 2).ElementAtOrDefault(1), NumberStyles.None, CultureInfo.InvariantCulture, out var sec) ? Math.Clamp(sec, 1, 600) : 10;
                        connection.Http.SimulateOutage(TimeSpan.FromSeconds(seconds));
                        Console.WriteLine($"  No connection to the server for {seconds} s (requests fail, websocket closed). Events are buffered.");
                        break;
                    default:
                        Console.WriteLine("  Unknown command. Type help. Commands for iRacing come from the race control page.");
                        break;
                }
            }
        }
        finally
        {
            await runtime.SleepAsync();
            runLog.Note("stop", null);
            Console.WriteLine($"Run log: {outbox}");
        }

        return 0;
    }
}
