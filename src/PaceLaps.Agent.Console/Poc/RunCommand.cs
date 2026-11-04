using System.Globalization;
using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.Core.Agent;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.IRacing.Win32;
using Microsoft.Extensions.Logging;

namespace PaceLaps.Agent.ConsoleHost.Poc;

/// <summary>Asks the operator in the console.</summary>
internal sealed class ConsolePrompt : IOperatorPrompt
{
    public Task<bool> ConfirmAsync(string question, CancellationToken ct) => Task.FromResult(Prompt.YesNo(question));
}

/// <summary>
/// The agent console: the real sim, typed and macro channels, and either the web service (<c>--server URL</c>, after
/// <c>pair</c>; roadmap step 3) or the proof of concept's in-process stub server (backlog 1.3), where the operator
/// types commands the way stewards would send them.
/// </summary>
internal static class RunCommand
{
    private const string Help = """
        Agent: activate | sleep | status | executor EPOCH [observer] | epoch N (epoch for the next commands) | lead MS | dup | quit
        Commands (sent through the stub server like a steward's action):
        """;

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        // The settings file, then the options on top (step 5: the tray app edits the same file).
        var config = new ConfigStore(ConfigStore.DefaultPath).Load();
        var macros = env.Args.Has("macros") ? ParseMacros(env.Args.Get("macros")) : config.Macros.ToTable();
        var serverUrl = env.Args.Has("stub") ? null : env.Args.Get("server") is { } raw ? PairCommand.ParseServer(raw) : config.Server;
        if (serverUrl is not null)
        {
            return await ServerRun.RunAsync(env, serverUrl, env.Args.Has("macros") ? macros : null);
        }

        var options = new AgentHostOptions
        {
            AgentId = env.Args.Get("agent-id") ?? "agt_poc",
            AgentVersion = SpikeEnvironment.AgentVersion,
            SelfTest = new SelfTestOptions
            {
                HarmlessCommand = env.Args.Get("harmless") ?? "!help",
                LeagueOverride = env.Args.Has("league") ? env.Args.GetInt("league", 0, 1, int.MaxValue) : config.League,
                MacroEnabled = macros.Count > 0,
                ReplayEnabled = !env.Args.Has("no-replay"),
            },
            Limits = Limits(env.Args),
            LocalFailsafe = config.LocalFailsafe,
        };
        var lead = env.Args.GetMs("lead", TimeSpan.FromMilliseconds(1500), 10_000);
        var offset = TimeSpan.FromMilliseconds(env.Args.GetInt("stub-offset-ms", 0, -60_000, 60_000));
        int? registered = env.Args.Has("stub-league") ? env.Args.GetInt("stub-league", 0, 1, int.MaxValue) : null;

        if (!await env.ConnectAsync(SimDataAdapter.TelemetryVars) || !env.PreflightTyping())
        {
            return 1;
        }

        var outboxDir = Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "poc-logs");
        var stamp = $"{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z";
        var outbox = Path.Combine(outboxDir, $"poc-run-{stamp}.jsonl");
        using var stub = new StubServer(outbox, env.Clock, offset, registered, executor: !env.Args.Has("observer"));
        Action<string, object?> note = stub.Note;
        note("start", new { options.AgentId, options.AgentVersion, macros = macros.Assignments, options.SelfTest, options.Limits, leadMs = lead.TotalMilliseconds, server = (string?)null, args = Environment.GetCommandLineArgs().Skip(1) });

        var sim = new SimDataAdapter(env.Sim);
        // After every typed line the chat box is read: a line the sim left there gets a second Enter, anything
        // else is cleared and reported (spike 15). --no-chat-check turns it off.
        var chatBox = env.Args.Has("no-chat-check") ? null : new ChatReadback(env.Focus, env.Keyboard, env.Sim, env.Clock);
        // Typed lines go without the focus switch unless the settings file or --typed-way focus say otherwise (spec-feedback 37).
        var typed = new TypedChatSender(env.Focus, env.Keyboard, env.Sim, env.Clock, env.TypedChatOptionsFromArgs(),
            env.LoggerFactory.CreateLogger<TypedChatSender>(), chatBox, new PostedChatInput())
        {
            WithoutFocus = env.TypedWithoutFocusFromArgs() ?? config.TypedWithoutFocus,
        };
        if (chatBox is not null)
        {
            Prompt.Warn("The agent reads the chat box through the clipboard after every typed line. Clipboard text is restored; images and files on it are not.");
        }
        var host = new AgentHost(options, sim, stub, typed, new SdkMacroChannel(env.Sim), macros, new SdkReplayChannel(env.Sim),
            new ConsolePrompt(), env.Clock, env.LoggerFactory, new AppIniMacroConfig(env.Args.Get("app-ini") ?? config.AppIni ?? AppIniMacroConfig.DefaultPath),
            new MemoryLocalFailsafeStore());
        host.States.Changed += (from, to, reason) =>
        {
            note("state", new { from, to, reason });
            Console.WriteLine($"[agent] {from} -> {to}: {reason}");
        };

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(env.Cancellation);
        var loop = host.RunAsync(stop.Token);

        Prompt.Heading("Proof-of-concept agent with a stub server");
        Console.WriteLine($"Run log: {outbox}");

        // Started from inside a packaged app (an MSIX app), Windows redirects this folder.
        Console.WriteLine($"Diagnostic log: {AgentLogging.LogDirectory}");
        Console.WriteLine(Help + CommandShorthand.Help);
        Console.WriteLine("The agent is Sleeping. Type 'activate' to run the self-test.");

        var n = 0;
        long? epochOverride = null;
        CommandMessage? last = null;
        try
        {
            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine();
                if (line is null)
                {
                    break;
                }

                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                note("input", new { line });
                var word = line.Split(' ', 2)[0].ToLowerInvariant();
                switch (word)
                {
                    case "quit" or "exit":
                        return 0;
                    case "help":
                        Console.WriteLine(Help + CommandShorthand.Help);
                        continue;
                    case "activate":
                        var report = await host.ActivateAsync(stop.Token);
                        if (report is null)
                        {
                            Console.WriteLine($"Cannot activate in state {host.States.State}.");
                            continue;
                        }

                        foreach (var step in AgentStatus.DescribeSteps(report))
                        {
                            Console.WriteLine("  " + step);
                        }

                        note("self_test", report);
                        continue;
                    case "sleep":
                        host.Sleep();
                        continue;
                    case "status":
                        Console.WriteLine($"  state {host.States.State}, executor {host.IsExecutor} epoch {host.Executor.Epoch}, channels {host.Channels}, " +
                            $"clock offset {host.ClockOffset.TotalMilliseconds:F0} ms, events buffered {host.Events.Count} (acknowledged up to {host.Events.LastAcked}), snapshots {stub.SnapshotCount}" +
                            (host.LastView is { Active: false, Reason: { } why } ? $", not active: {why}" : "") +
                            (host.States.LastFailure is { } f ? $", last failure: {f}" : ""));
                        Console.WriteLine($"  local failsafe {(options.LocalFailsafe.Enabled ? $"on (after {options.LocalFailsafe.DelaySeconds} s)" : "off")}, " +
                            (host.LocalFailsafeCopies.Held is { } held ? $"holds {held.Procedure} v{held.Version}" : "holds no copy") +
                            (host.LocalFailsafe.Since is { } since ? $", RUNNING since {since.ToLocalTime():HH:mm:ss}" : ""));
                        continue;
                    case "executor":
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
                        {
                            Console.WriteLine("usage: executor EPOCH [observer]");
                            continue;
                        }

                        stub.Assign(epoch, !(parts.Length > 2 && parts[2] == "observer"));
                        continue;
                    case "epoch":
                        epochOverride = long.TryParse(line.Split(' ', 2).ElementAtOrDefault(1), NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? e : null;
                        Console.WriteLine(epochOverride is null ? "Commands use the agent's epoch." : $"Next commands carry epoch {epochOverride}.");
                        continue;
                    case "lead":
                        lead = TimeSpan.FromMilliseconds(int.TryParse(line.Split(' ', 2).ElementAtOrDefault(1), out var ms) ? Math.Clamp(ms, 0, 10_000) : 1500);
                        Console.WriteLine($"Execute-at lead {lead.TotalMilliseconds:F0} ms.");
                        continue;
                    case "dup":
                        if (last is null)
                        {
                            Console.WriteLine("No command sent yet.");
                            continue;
                        }

                        stub.Deliver(last);
                        continue;
                }

                var parsed = CommandShorthand.Parse(line, out var error);
                if (parsed is not { } p)
                {
                    Console.WriteLine($"  {error}. Type help.");
                    continue;
                }

                // The server's clock is this PC's clock plus the stub offset; execute-at uses the server clock.
                last = new CommandMessage
                {
                    Id = $"cmd_{++n}",
                    Epoch = epochOverride ?? host.Executor.Epoch,
                    Type = p.Type,
                    Params = p.Params,
                    Idempotent = CommandCatalogue.Idempotent(p.Type),
                    IssuedAt = env.Clock.UtcNow + offset,
                    ExecuteAt = env.Clock.UtcNow + offset + lead,
                    IssuedBy = "steward:console",
                };
                stub.Deliver(last);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await loop;
            note("stop", null);
            Console.WriteLine($"Run log: {outbox}");
        }

        return 0;
    }

    /// <summary>--max-chat and --max-pace-laps on top of the defaults.</summary>
    public static CommandLimits Limits(CliArgs args) => new()
    {
        MaxChatLength = args.GetInt("max-chat", new CommandLimits().MaxChatLength, 20, 2000),
        MaxPaceLaps = args.GetInt("max-pace-laps", new CommandLimits().MaxPaceLaps, 1, 999),
    };

    /// <summary>"14=!pitclose;15=!pitopen" into a macro table. The sim slots must contain the text followed by $.</summary>
    public static MacroTable ParseMacros(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return MacroTable.Empty;
        }

        var list = new List<(int, string)>();
        foreach (var item in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = item.IndexOf('=', StringComparison.Ordinal);
            if (eq < 1 || !int.TryParse(item[..eq], NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot is < 1 or > 15)
            {
                throw new CliException($"--macros: '{item}' is not SLOT=TEXT with a slot from 1 to 15");
            }

            list.Add((slot, item[(eq + 1)..].Trim()));
        }

        try
        {
            return new MacroTable(list);
        }
        catch (ArgumentException e)
        {
            throw new CliException("--macros: " + e.Message);
        }
    }
}
