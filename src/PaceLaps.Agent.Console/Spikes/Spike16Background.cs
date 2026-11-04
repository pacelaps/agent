using System.Diagnostics;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Spike 16 (added 2026-10-28): can the agent get a chat line into iRacing without bringing it to the front?
/// the other tool does (the operator saw it), and the typed channel's focus switch feels clunky.
///
/// Part monitor: watches passively while the operator sends messages from another program (the other tool): the
/// front window, injected keys and mouse, clipboard changes, the SDK's broadcast messages and app.ini writes.
/// Part send: tries ways that need no focus switch (window messages: WM_CHAR, WM_PASTE, Enter as key or
/// character) and, for comparison, the paste that iRaceDeck uses (focus, Ctrl+V, Enter). Each line is a numbered
/// /rc message the operator ticks off; after each, the chat box is read back (spike 15) for text left in it.
/// </summary>
internal static class Spike16Background
{
    private static readonly (string Code, string Name, string About)[] Variants =
    [
        ("A", "postchar-key", "begin chat (SDK), WM_CHAR per character, Enter as WM_KEYDOWN/UP"),
        ("B", "postchar-char", "begin chat (SDK), WM_CHAR per character, Enter as WM_CHAR"),
        ("C", "postpaste", "begin chat (SDK), clipboard, WM_PASTE, Enter as WM_KEYDOWN/UP"),
        ("D", "paste-focus", "iRacing to the front, begin chat, Ctrl+V, Enter, previous window back (iRaceDeck's way)"),
    ];

    private const uint FlagCaution = 0x00004000;
    private const uint FlagCautionWaving = 0x00008000;
    private const uint FlagBlack = 0x00010000;

    /// <summary>Halves of the soak: posted in queue order with no waits, then with waits as a margin.</summary>
    private static readonly (string Code, string About, TimeSpan OpenDelay, TimeSpan CancelDelay)[] SoakHalves =
    [
        ("Q", "no waits: begin chat, text, Enter, cancel chat in queue order", TimeSpan.Zero, TimeSpan.Zero),
        ("W", "waits: 50 ms after begin chat, cancel chat 100 ms after Enter", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)),
    ];

    /// <summary>
    /// Forms of the driving part: waits after begin chat and before cancel chat, posted or sent. In the soak, no-wait
    /// lines arrived out of the car but none while the operator was in it; the operator saw the other tool's chat box
    /// open for a few frames there.
    /// </summary>
    private static readonly (string Code, MessageDelivery Delivery, TimeSpan OpenDelay, TimeSpan CancelDelay)[] DrivingForms =
    [
        ("0", MessageDelivery.Post, TimeSpan.Zero, TimeSpan.Zero),
        ("1", MessageDelivery.Post, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)),
        ("2", MessageDelivery.Post, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)),
        ("3", MessageDelivery.Post, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500)),
        ("S", MessageDelivery.Send, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300)),
    ];

    private static readonly string[] CarStateVars = ["IsOnTrack", "IsInGarage", "PlayerCarInPitStall", "OnPitRoad"];

    public static Task<int> RunAsync(SpikeEnvironment env) => env.Args.GetChoice("part", "monitor", "send", "soak", "driving") switch
    {
        "monitor" => MonitorAsync(env),
        "send" => SendAsync(env),
        "driving" => DrivingAsync(env),
        _ => SoakAsync(env),
    };

    /// <summary>
    /// Part driving (after the soak): the operator in the car. A: the monitor times the other tool's begin chat to
    /// cancel chat in the car. B: parked in the pit stall, another window in front, five forms. C: driving with
    /// iRacing in front, the same forms. Every line logs where the player's car is, from telemetry.
    /// </summary>
    private static async Task<int> DrivingAsync(SpikeEnvironment env)
    {
        var parkedReps = env.Args.GetInt("parked-reps", 3, 1, 10);
        var drivingReps = env.Args.GetInt("driving-reps", 2, 0, 10);
        var interval = env.Args.GetSeconds("interval", TimeSpan.FromSeconds(3), 60);
        if (!await env.ConnectAsync([.. CarStateVars, "SessionFlags"]) || !env.PreflightTyping())
        {
            return 1;
        }

        using var log = env.CreateLog(16);
        var sim = env.Focus.FindSimWindow();
        log.Write("header", env.Header(16, log.RunId, new
        {
            part = "driving",
            parkedReps,
            drivingReps,
            intervalSeconds = interval.TotalSeconds,
            forms = DrivingForms.Select(f => new { f.Code, f.Delivery, openDelayMs = f.OpenDelay.TotalMilliseconds, cancelDelayMs = f.CancelDelay.TotalMilliseconds }).ToArray(),
            simWindow = DescribeSim(sim),
        }));
        Console.WriteLine($"Log: {log.Path}");

        using var monitor = new InputMonitor();
        if (await monitor.StartAsync() is { } problem)
        {
            log.Write("monitor_problem", new { problem });
        }

        var counts = new Dictionary<string, int>();
        var seenEvents = new List<MonitorEvent>();
        var drain = DrainAsync(monitor, log, counts, echo: false, env.Cancellation, e =>
        {
            lock (seenEvents)
            {
                seenEvents.Add(e);
            }
        });

        object CarState() => CarStateVars.ToDictionary(v => v, v => env.Sim.Latest?.Get(v));

        // Phase A: the other tool in the car, timed by the monitor.
        Prompt.Heading("Phase A: the other tool while you are in the car");
        Console.WriteLine("Get in the car (on track or parked in your pit stall). From the other tool, send an /rc message and a public message,");
        Console.WriteLine("a few seconds apart. Then come back here and press Enter.");
        var phaseAFrom = DateTimeOffset.UtcNow;
        log.Write("phaseA_start", new { car = CarState() });
        Prompt.Ask("  [Enter] when the other tool has sent its messages:");
        List<MonitorEvent> chatEvents;
        lock (seenEvents)
        {
            chatEvents = seenEvents.Where(e => e.At >= phaseAFrom && e.Kind == "sdk_broadcast").ToList();
        }

        var gapsMs = ChatGaps(chatEvents);
        var phaseAArrived = Prompt.Ask("Which of the other tool's messages appeared in chat, and was the chat box visible for a moment?");
        log.Write("phaseA", new { car = CarState(), beginToCancelMs = gapsMs, arrived = phaseAArrived });
        Console.WriteLine($"  the other tool's begin chat to cancel chat: {(gapsMs.Count == 0 ? "none seen" : string.Join(", ", gapsMs) + " ms")}");

        // Phase B: parked in the pit stall, another window in front.
        Prompt.Heading($"Phase B: parked in your pit stall, another window in front ({DrivingForms.Length} forms x {parkedReps})");
        var number = 0;
        await env.CountdownAsync(TimeSpan.FromSeconds(15),
            "Drive into your pit stall and stop there (stay in the car). Then click into your browser, keep iRacing's chat visible, and do not touch anything.");
        var phaseB = await SendFormsAsync(env, log, "B", parkedReps, interval, CarState, () => ++number);
        Console.Beep();
        Prompt.Heading("Phase B done. Switch back to this window:");
        var missingB = Prompt.NumberList($"Which numbers did NOT appear in chat (1 to {number}; blank for none)?", number);
        log.Write("phaseB", new { first = 1, last = number, missing = missingB, lines = phaseB });

        // Phase C: driving with iRacing in front.
        object? phaseC = null;
        if (drivingReps > 0)
        {
            var first = number + 1;
            await env.CountdownAsync(TimeSpan.FromSeconds(15), "Click into iRacing and drive out of the pits. Keep driving until the two beeps.");
            var lines = await SendFormsAsync(env, log, "C", drivingReps, interval, CarState, () => ++number);
            Console.Beep();
            Console.Beep();
            Prompt.Heading("Phase C done. Stop in a safe place, switch back to this window:");
            var missingC = Prompt.NumberList($"Which numbers did NOT appear in chat ({first} to {number}; blank for none)?", number);
            var disturbed = Prompt.Ask("Did anything disturb driving, or did the chat box stay open? Blank for nothing:");
            phaseC = new { first, last = number, missing = missingC, disturbed, lines };
            log.Write("phaseC", phaseC);
        }

        monitor.Dispose();
        await drain;
        var notes = Prompt.Ask("Notes (blank for none):");
        log.Write("summary", new { otherToolBeginToCancelMs = gapsMs, missingB, phaseCDone = phaseC is not null, notes, monitorCounts = counts });
        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }

    /// <summary>Every form <paramref name="reps"/> times, round-robin, one numbered line each.</summary>
    private static async Task<List<object>> SendFormsAsync(SpikeEnvironment env, SpikeLog log, string phase, int reps, TimeSpan interval,
        Func<object> carState, Func<int> next)
    {
        var lines = new List<object>();
        for (var rep = 1; rep <= reps; rep++)
        {
            foreach (var f in DrivingForms)
            {
                var number = next();
                var text = $"/rc s16 d{f.Code} {number:00}";
                var simInFront = env.Focus.GetForeground() == env.Focus.FindSimWindow();
                var sent = await SendLineAsync(env, text, f.Delivery, f.OpenDelay, f.CancelDelay);
                var line = new { number, phase, form = f.Code, text, simInFront, car = carState(), sent };
                lines.Add(line);
                log.Write("line", line);
                Console.WriteLine($"  {number:00} {text}{(simInFront ? "" : " (iRacing not in front)")}");
                await env.Clock.Delay(interval, env.Cancellation);
            }
        }

        return lines;
    }

    /// <summary>Milliseconds from each begin chat to the next cancel chat among the SDK broadcasts.</summary>
    internal static List<long> ChatGaps(IEnumerable<MonitorEvent> broadcasts)
    {
        var gaps = new List<long>();
        DateTimeOffset? begin = null;
        foreach (var e in broadcasts.OrderBy(e => e.At))
        {
            var description = System.Text.Json.JsonSerializer.Serialize(e.Data);
            if (description.Contains("chat BeginChat", StringComparison.Ordinal))
            {
                begin = e.At;
            }
            else if (description.Contains("chat Cancel", StringComparison.Ordinal) && begin is { } b)
            {
                gaps.Add((long)(e.At - b).TotalMilliseconds);
                begin = null;
            }
        }

        return gaps;
    }

    /// <summary>
    /// Part soak (after part send passed): variant A at length. 50 numbered lines in two halves, real admin commands
    /// checked in telemetry, special characters, and lines while the operator drives. No readback: the agent would
    /// have none without the focus; cancel chat closes the box after each line.
    /// </summary>
    private static async Task<int> SoakAsync(SpikeEnvironment env)
    {
        var lines = env.Args.GetInt("lines", 25, 1, 100);
        var interval = env.Args.GetSeconds("interval", TimeSpan.FromSeconds(2), 60);
        var startDelay = env.Args.GetSeconds("start-delay", TimeSpan.FromSeconds(10), 120);
        var reps = env.Args.GetInt("reps", 3, 1, 10);
        var drivingLines = env.Args.GetInt("driving-lines", 10, 0, 50);
        var delivery = env.Args.Has("delivery") && env.Args.GetChoice("delivery", "post", "send") == "send" ? MessageDelivery.Send : MessageDelivery.Post;
        if (!await env.ConnectAsync(["SessionFlags", "CarIdxSessionFlags"]) || !env.PreflightTyping())
        {
            return 1;
        }

        using var log = env.CreateLog(16);
        var sim = env.Focus.FindSimWindow();
        log.Write("header", env.Header(16, log.RunId, new
        {
            part = "soak",
            lines,
            intervalSeconds = interval.TotalSeconds,
            reps,
            drivingLines,
            delivery,
            halves = SoakHalves.Select(h => new { h.Code, h.About, openDelayMs = h.OpenDelay.TotalMilliseconds, cancelDelayMs = h.CancelDelay.TotalMilliseconds }).ToArray(),
            simWindow = DescribeSim(sim),
        }));
        Console.WriteLine($"Log: {log.Path}");

        using var monitor = new InputMonitor();
        if (await monitor.StartAsync() is { } problem)
        {
            log.Write("monitor_problem", new { problem });
        }

        var counts = new Dictionary<string, int>();
        var seenEvents = new List<MonitorEvent>();
        var drain = DrainAsync(monitor, log, counts, echo: false, env.Cancellation, e =>
        {
            lock (seenEvents)
            {
                seenEvents.Add(e);
            }
        });

        // What the monitor saw in a phase: front window changes and injected keys. Both should be zero.
        object PhaseMonitor(DateTimeOffset from, DateTimeOffset to)
        {
            lock (seenEvents)
            {
                var inPhase = seenEvents.Where(e => e.At >= from && e.At <= to).ToList();
                return new
                {
                    foregroundChanges = inPhase.Count(e => e.Kind == "foreground"),
                    injectedKeys = inPhase.Count(e => e.Kind == "key" && System.Text.Json.JsonSerializer.Serialize(e.Data).Contains("\"injected\":true", StringComparison.Ordinal)),
                    clipboardChanges = inPhase.Count(e => e.Kind == "clipboard"),
                };
            }
        }

        // Phase 1: numbered lines with another window in front.
        Prompt.Heading($"Phase 1: {lines * SoakHalves.Length} numbered /rc lines, iRacing NOT in front");
        foreach (var h in SoakHalves)
        {
            Console.WriteLine($"  {h.Code}: {h.About}");
        }

        await env.CountdownAsync(startDelay,
            "Click into your browser (or any window that is not iRacing) and keep iRacing's chat visible. Do not touch keyboard or mouse.");
        var phase1From = DateTimeOffset.UtcNow;
        var number = 0;
        var skipped = new List<int>();
        var simCameForward = 0;
        foreach (var h in SoakHalves)
        {
            for (var i = 1; i <= lines; i++)
            {
                number++;
                var text = $"/rc s16 {h.Code} {number:00}";
                if (env.Focus.GetForeground() == env.Focus.FindSimWindow())
                {
                    skipped.Add(number);
                    log.Write("line", new { number, half = h.Code, skipped = "iRacing was in front" });
                    Console.WriteLine($"  {number:00} skipped: iRacing is in front. Click another window.");
                }
                else
                {
                    var sent = await SendLineAsync(env, text, delivery, h.OpenDelay, h.CancelDelay);
                    var cameForward = env.Focus.GetForeground() == env.Focus.FindSimWindow();
                    simCameForward += cameForward ? 1 : 0;
                    log.Write("line", new { number, half = h.Code, text, sent, cameForward });
                    Console.WriteLine($"  {number:00} {text}{(cameForward ? "  iRacing came to the front!" : "")}");
                }

                await env.Clock.Delay(interval, env.Cancellation);
            }
        }

        var phase1Monitor = PhaseMonitor(phase1From, DateTimeOffset.UtcNow);
        Console.Beep();
        Prompt.Heading("Phase 1 done. Switch back to this window and answer:");
        var missing = Prompt.NumberList($"Which numbers did NOT appear in iRacing chat (for example 7,31; blank for none; 1 to {number})?", number);
        var bindingFired = Prompt.Int("How many times did your test binding's line ('/rc BINDING FIRED') appear (0 if none, or no binding set)?", 0, 1000);
        var phase1LeftOpen = Prompt.YesNo("Was iRacing's chat box left open at any point?");
        log.Write("phase1", new { sentLines = number - skipped.Count, skipped, missing, bindingFired, chatLeftOpen = phase1LeftOpen, simCameForward, monitor = phase1Monitor });

        // Phase 2: real admin commands, checked in telemetry.
        var session = env.Sim.Session!;
        var numbers = session.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar).Select(c => c.CarNumber).ToList();
        Prompt.Heading("Phase 2: !black and !clear on one car, special characters, and optionally a caution with a message");
        Console.WriteLine("Cars: " + string.Join(" ", numbers.Select(n => "#" + n)));
        bool IsCar(string? n) => n is not null && Guided.GuidedSteps.IsValidCarNumber(n) && session.CarByNumber(n) is not null;
        var car = env.Args.Get("car");
        if (car is not null && !IsCar(car))
        {
            throw new Cli.CliException($"--car {car} is not a car in this session");
        }

        while (car is null)
        {
            var answer = Prompt.Ask("Car for the black flag (an AI car under green):");
            if (IsCar(answer))
            {
                car = answer;
            }
            else
            {
                Console.WriteLine("  Not a car in this session.");
            }
        }

        var idx = session.CarByNumber(car)!.CarIdx;
        await env.CountdownAsync(startDelay, "Under green. Click into your browser again and do not touch keyboard or mouse.");
        var phase2From = DateTimeOffset.UtcNow;
        var commands = new List<object>();
        for (var rep = 1; rep <= reps; rep++)
        {
            foreach (var (text, wantBlack) in new[] { ($"!black #{car} D", true), ($"!clear #{car}", false) })
            {
                var sent = await SendLineAsync(env, text, delivery, SoakHalves[0].OpenDelay, SoakHalves[0].CancelDelay);
                var after = await WaitForAsync(env, f => (Bits(f, "CarIdxSessionFlags", idx) & FlagBlack) != 0 == wantBlack);
                commands.Add(new { rep, text, sent, verified = after is not null, afterSeconds = after });
                log.Write("command", commands[^1]);
                Console.WriteLine($"  {text,-14} {(after is null ? "NOT seen in telemetry" : $"seen after {after} s")}");
                await env.Clock.Delay(interval, env.Cancellation);
            }
        }

        // Unicode and punctuation: posted as characters, so the keyboard layout should not matter.
        const string special = "/rc s16 Zeichen: äöüß é # ! ? , . : ; ( ) - + = @ 60";
        var specialSent = await SendLineAsync(env, special, delivery, SoakHalves[0].OpenDelay, SoakHalves[0].CancelDelay);
        log.Write("special", new { text = special, sent = specialSent });
        await env.Clock.Delay(interval, env.Cancellation);

        object? caution = null;
        var phase2Monitor = PhaseMonitor(phase2From, DateTimeOffset.UtcNow);
        Console.Beep();
        Prompt.Heading("Switch back to this window:");
        var specialExact = Prompt.YesNo($"Did this line appear exactly, every character:  {special.Replace("/rc ", "", StringComparison.Ordinal)}");
        var specialSeen = specialExact ? special : Prompt.Ask("What appeared instead (blank if nothing)?");
        if (Prompt.YesNo("Throw a full course caution with a message now (the runner sends !yellow)?"))
        {
            await env.CountdownAsync(TimeSpan.FromSeconds(5), "Click into your browser again.");
            const string yellow = "!yellow Spike 16 test caution, please ignore";
            var sent = await SendLineAsync(env, yellow, delivery, SoakHalves[0].OpenDelay, SoakHalves[0].CancelDelay);
            var after = await WaitForAsync(env, f => (Bits(f, "SessionFlags", 0) & (FlagCaution | FlagCautionWaving)) != 0);
            Console.WriteLine($"  {yellow}: {(after is null ? "caution NOT seen in telemetry" : $"caution after {after} s")}");
            var messageShown = Prompt.YesNo("Did iRacing show the caution's message ('Spike 16 test caution, please ignore')?");
            caution = new { text = yellow, sent, verified = after is not null, afterSeconds = after, messageShown };
        }

        log.Write("phase2", new { car, commands, special = new { exact = specialExact, seen = specialSeen }, caution, monitor = phase2Monitor });

        // Phase 3: while the operator drives, iRacing is in front anyway; the question is whether it disturbs driving.
        object? phase3 = null;
        if (drivingLines > 0 && Prompt.YesNo($"Phase 3: send {drivingLines} lines while you drive?"))
        {
            await env.CountdownAsync(TimeSpan.FromSeconds(15), "Click into iRacing and drive (pits exit is fine). Keep driving until the beep.");
            var drivingFrom = DateTimeOffset.UtcNow;
            var first = number + 1;
            for (var i = 1; i <= drivingLines; i++)
            {
                number++;
                var text = $"/rc s16 R {number:00}";
                var inFront = env.Focus.GetForeground() == env.Focus.FindSimWindow();
                var sent = await SendLineAsync(env, text, delivery, SoakHalves[0].OpenDelay, SoakHalves[0].CancelDelay);
                log.Write("line", new { number, half = "R", text, sent, simInFront = inFront });
                await env.Clock.Delay(TimeSpan.FromSeconds(3), env.Cancellation);
            }

            var drivingMonitor = PhaseMonitor(drivingFrom, DateTimeOffset.UtcNow);
            Console.Beep();
            Console.Beep();
            Prompt.Heading("Phase 3 done. Stop in a safe place, switch back to this window and answer:");
            var drivingMissing = Prompt.NumberList($"Which numbers did NOT appear in chat ({first} to {number}; blank for none)?", number);
            var disturbed = Prompt.Ask("Did anything disturb driving (stutter, a control, the chat box staying open, a key binding)? Blank for nothing:");
            phase3 = new { first, last = number, missing = drivingMissing, disturbed, monitor = drivingMonitor };
            log.Write("phase3", phase3);
        }

        monitor.Dispose();
        await drain;

        var notes = Prompt.Ask("Notes (blank for none):");
        log.Write("summary", new { phase1 = new { sent = lines * SoakHalves.Length - skipped.Count, missing, bindingFired, simCameForward }, phase2Commands = commands.Count, specialExact, phase3Done = phase3 is not null, notes, monitorCounts = counts });
        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }

    /// <summary>
    /// One chat line without the focus: begin chat (SDK), the text as WM_CHAR, Enter as WM_KEYDOWN/UP, cancel chat
    /// (SDK) so the box is never left open. With posted messages the order holds by itself (the SDK's broadcasts are
    /// posted too); the waits are a margin to compare against.
    /// </summary>
    private static async Task<object> SendLineAsync(SpikeEnvironment env, string text, MessageDelivery delivery, TimeSpan openDelay, TimeSpan cancelDelay)
    {
        var sim = env.Focus.FindSimWindow();
        var watch = Stopwatch.StartNew();
        env.Sim.BeginChat();
        if (openDelay > TimeSpan.Zero)
        {
            await env.Clock.Delay(openDelay, env.Cancellation);
        }

        var typed = WindowMessageTyping.PostText(sim.Value, text, delivery);
        var enter = WindowMessageTyping.PostEnter(sim.Value, PostedEnter.Key, delivery);
        if (cancelDelay > TimeSpan.Zero)
        {
            await env.Clock.Delay(cancelDelay, env.Cancellation);
        }

        env.Sim.CancelChat();
        return new { typed, enter, elapsedMs = watch.ElapsedMilliseconds };
    }

    /// <summary>Seconds until the telemetry shows the effect, or null after 3 s.</summary>
    private static async Task<double?> WaitForAsync(SpikeEnvironment env, Func<TelemetryFrame, bool> effect)
    {
        var from = env.Clock.Elapsed;
        while (env.Clock.Elapsed - from < TimeSpan.FromSeconds(3))
        {
            if (env.Sim.Latest is { } f && effect(f))
            {
                return Math.Round((env.Clock.Elapsed - from).TotalSeconds, 2);
            }

            await env.Clock.Delay(TimeSpan.FromMilliseconds(50), env.Cancellation);
        }

        return null;
    }

    private static uint Bits(TelemetryFrame f, string name, int idx) =>
        f.Get(name, idx) is { } v ? Convert.ToUInt32(v, System.Globalization.CultureInfo.InvariantCulture) : 0u;

    private static async Task<int> MonitorAsync(SpikeEnvironment env)
    {
        var minutes = env.Args.GetInt("minutes", 10, 1, 60);
        if (!await env.ConnectAsync([]))
        {
            return 1;
        }

        using var log = env.CreateLog(16);
        var sim = env.Focus.FindSimWindow();
        log.Write("header", env.Header(16, log.RunId, new { part = "monitor", minutes, simWindow = DescribeSim(sim) }));
        Console.WriteLine($"Log: {log.Path}");

        using var monitor = new InputMonitor();
        var problem = await monitor.StartAsync();
        if (problem is not null)
        {
            Prompt.Warn("Monitor: " + problem);
            log.Write("monitor_problem", new { problem });
        }

        using var appIni = WatchAppIni(log);
        var counts = new Dictionary<string, int>();
        var drain = DrainAsync(monitor, log, counts, echo: true, env.Cancellation);

        Prompt.Heading($"Spike 16, part monitor: watching for up to {minutes} min");
        Console.WriteLine("Send your messages from the other tool now (see the steps). Every event appears here and in the log.");
        Console.WriteLine("Back in this window: Enter adds a note (what you just sent), Q ends the watch.");

        var until = env.Clock.Elapsed + TimeSpan.FromMinutes(minutes);
        while (env.Clock.Elapsed < until)
        {
            if (Prompt.KeyIfAvailable() is { } key)
            {
                if (key.Key == ConsoleKey.Q)
                {
                    break;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    var note = Prompt.Ask("Note:");
                    log.Write("note", new { note });
                }
            }

            await env.Clock.Delay(TimeSpan.FromMilliseconds(100), env.Cancellation);
        }

        monitor.Dispose();
        await drain;

        Console.Beep();
        Prompt.Heading("Done. Answer for the log:");
        var sent = Prompt.Ask("Which messages did you send from the other tool (kind and text), and did each appear in chat?");
        var simCameForward = Prompt.YesNo("Did iRacing come to the front at any point?");
        var notes = Prompt.Ask("Notes (blank for none):");
        log.Write("summary", new { counts, sent, simCameForward, notes });

        Console.WriteLine($"Events: {string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}");
        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }

    private static async Task<int> SendAsync(SpikeEnvironment env)
    {
        var count = env.Args.GetInt("count", 3, 1, 20);
        var interval = env.Args.GetSeconds("interval", TimeSpan.FromSeconds(4), 60);
        var startDelay = env.Args.GetSeconds("start-delay", TimeSpan.FromSeconds(10), 120);
        var openDelay = env.Args.GetMs("chat-open-delay", TimeSpan.FromMilliseconds(150), 2000);
        var settle = env.Args.GetMs("settle", TimeSpan.FromMilliseconds(400), 5000);
        var readback = !env.Args.Has("no-readback");
        var chosen = (env.Args.Get("variants") ?? "A,B,C,D").ToUpperInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var variants = Variants.Where(v => chosen.Contains(v.Code)).ToArray();
        if (variants.Length == 0)
        {
            throw new Cli.CliException("--variants: one or more of A, B, C, D");
        }

        if (!await env.ConnectAsync([]) || !env.PreflightTyping())
        {
            return 1;
        }

        using var log = env.CreateLog(16);
        var sim = env.Focus.FindSimWindow();
        log.Write("header", env.Header(16, log.RunId, new
        {
            part = "send",
            count,
            intervalSeconds = interval.TotalSeconds,
            openDelayMs = openDelay.TotalMilliseconds,
            settleMs = settle.TotalMilliseconds,
            readback,
            variants = variants.Select(v => new { v.Code, v.Name, v.About }).ToArray(),
            simWindow = DescribeSim(sim),
        }));
        Console.WriteLine($"Log: {log.Path}");

        // The monitor runs along: it shows whether a variant moved the front window or injected keys.
        using var monitor = new InputMonitor();
        if (await monitor.StartAsync() is { } problem)
        {
            log.Write("monitor_problem", new { problem });
        }

        var counts = new Dictionary<string, int>();
        var drain = DrainAsync(monitor, log, counts, echo: false, env.Cancellation);
        var reader = new ChatReadback(env.Focus, env.Keyboard, env.Sim, env.Clock);

        Prompt.Heading($"Spike 16, part send: {variants.Length} variants x {count} lines");
        foreach (var v in variants)
        {
            Console.WriteLine($"  {v.Code} {v.Name}: {v.About}");
        }

        await env.CountdownAsync(startDelay,
            "Click into your browser (or any window that is not iRacing) and keep iRacing's chat visible. Do not touch keyboard or mouse.");

        var number = 0;
        var attempts = new List<Attempt>();
        foreach (var v in variants)
        {
            Console.WriteLine($"Variant {v.Code} ({v.Name})");
            for (var i = 1; i <= count; i++)
            {
                number++;
                var text = $"/rc s16 {v.Code} {number:00}";
                var before = env.Focus.GetForeground();
                if (before == env.Focus.FindSimWindow())
                {
                    log.Write("attempt", new { number, variant = v.Code, skipped = "iRacing was in front" });
                    Console.WriteLine($"  {number:00} skipped: iRacing is in front. Click another window.");
                    attempts.Add(new Attempt(number, v.Code, Skipped: true, SimCameForward: false, Leftover: null));
                }
                else
                {
                    var attempt = await SendOneAsync(env, v.Code, number, text, openDelay, settle);
                    string? leftover = null;
                    var readbackOutcome = "skipped";
                    if (readback)
                    {
                        var r = await reader.ReadAndCloseAsync(openChat: true, env.Cancellation);
                        readbackOutcome = r.Outcome.ToString();
                        leftover = r.Outcome == ReadbackOutcome.Text ? r.Text : null;
                    }
                    else
                    {
                        // Never leave the chat box open.
                        env.Sim.CancelChat();
                    }

                    log.Write("attempt", new { number, variant = v.Code, text, attempt.Steps, attempt.SimCameForward, attempt.ElapsedMs, readback = readbackOutcome, leftover });
                    Console.WriteLine($"  {number:00} {text}: {attempt.ElapsedMs} ms{(attempt.SimCameForward ? ", iRacing came to the front" : "")}"
                        + (leftover is null ? "" : $", left in chat: '{leftover}'"));
                    attempts.Add(new Attempt(number, v.Code, Skipped: false, attempt.SimCameForward, leftover));
                }

                await env.Clock.Delay(interval, env.Cancellation);
            }
        }

        monitor.Dispose();
        await drain;

        Console.Beep();
        Prompt.Heading("Done. Switch back to this window and answer:");
        var seen = Prompt.NumberList($"Which numbers appeared in iRacing chat (for example 1-3,7; blank for none; 1 to {number})?", number);
        var chatLeftOpen = Prompt.YesNo("Was iRacing's chat box left open at any point after a line?");
        var sideEffects = Prompt.Ask("Anything else happen in the sim (a camera, a menu, a car control, a key binding)? Blank for nothing:");
        var notes = Prompt.Ask("Notes (blank for none):");

        var perVariant = variants.Select(v =>
        {
            var mine = attempts.Where(a => a.Variant == v.Code).ToList();
            var sent = mine.Where(a => !a.Skipped).ToList();
            return new
            {
                variant = v.Code,
                name = v.Name,
                sent = sent.Count,
                skipped = mine.Count - sent.Count,
                seen = sent.Count(a => seen.Contains(a.Number)),
                leftInChat = sent.Count(a => a.Leftover is not null),
                simCameForward = sent.Count(a => a.SimCameForward),
            };
        }).ToArray();
        log.Write("summary", new { perVariant, seen, chatLeftOpen, sideEffects, notes, monitorCounts = counts });

        Console.WriteLine();
        foreach (var r in perVariant)
        {
            Console.WriteLine($"  {r.variant} {r.name}: seen {r.seen} of {r.sent}, left in chat {r.leftInChat}, iRacing came forward {r.simCameForward}x");
        }

        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }

    private static async Task<(object Steps, bool SimCameForward, long ElapsedMs)> SendOneAsync(SpikeEnvironment env, string variant,
        int number, string text, TimeSpan openDelay, TimeSpan settle)
    {
        var sim = env.Focus.FindSimWindow();
        var before = env.Focus.GetForeground();
        var watch = Stopwatch.StartNew();
        object steps;
        var savedClipboard = variant is "C" or "D" ? ClipboardText.Get() : null;

        switch (variant)
        {
            case "A":
            case "B":
            {
                env.Sim.BeginChat();
                await env.Clock.Delay(openDelay, env.Cancellation);
                var typed = WindowMessageTyping.PostText(sim.Value, text);
                await env.Clock.Delay(TimeSpan.FromMilliseconds(50), env.Cancellation);
                var enter = WindowMessageTyping.PostEnter(sim.Value, variant == "A" ? PostedEnter.Key : PostedEnter.Char);
                steps = new { typed, enter };
                break;
            }

            case "C":
            {
                var clipboard = ClipboardText.Set(text);
                env.Sim.BeginChat();
                await env.Clock.Delay(openDelay, env.Cancellation);
                var paste = WindowMessageTyping.PostPaste(sim.Value);
                await env.Clock.Delay(TimeSpan.FromMilliseconds(50), env.Cancellation);
                var enter = WindowMessageTyping.PostEnter(sim.Value, PostedEnter.Key);
                steps = new { clipboard, paste, enter };
                break;
            }

            default:
            {
                var clipboard = ClipboardText.Set(text);
                var focusMethod = env.Focus.BringToFront(sim);
                var until = env.Clock.Elapsed + TimeSpan.FromSeconds(1);
                while (env.Focus.GetForeground() != sim && env.Clock.Elapsed < until)
                {
                    await env.Clock.Delay(TimeSpan.FromMilliseconds(10), env.Cancellation);
                }

                var focused = env.Focus.GetForeground() == sim;
                var focusMs = watch.ElapsedMilliseconds;
                bool paste = false, enter = false;
                if (focused)
                {
                    env.Sim.BeginChat();
                    await env.Clock.Delay(openDelay, env.Cancellation);
                    paste = env.Keyboard.SendCtrlChord(ScancodeKeyboard.ScanV);
                    await env.Clock.Delay(TimeSpan.FromMilliseconds(30), env.Cancellation);
                    enter = env.Keyboard.SendEnter();
                    await env.Clock.Delay(TimeSpan.FromMilliseconds(100), env.Cancellation);
                }

                env.Focus.BringToFront(before);
                steps = new { clipboard, focusMethod, focused, focusMs, paste, enter, inFrontMs = watch.ElapsedMilliseconds - focusMs };
                break;
            }
        }

        var elapsed = watch.ElapsedMilliseconds;
        await env.Clock.Delay(settle, env.Cancellation);
        if (variant is "C" or "D")
        {
            // The operator's clipboard text back (other formats are not kept, as in the readback).
            ClipboardText.Set(savedClipboard);
        }

        // Variant D brings iRacing forward on purpose; for the others this is the question.
        var cameForward = variant != "D" && env.Focus.GetForeground() == sim;
        return (steps, cameForward, elapsed);
    }

    private static async Task DrainAsync(InputMonitor monitor, SpikeLog log, Dictionary<string, int> counts, bool echo, CancellationToken ct,
        Action<MonitorEvent>? observe = null)
    {
        try
        {
            await foreach (var e in monitor.Events.ReadAllAsync(ct))
            {
                observe?.Invoke(e);
                counts[e.Kind] = counts.GetValueOrDefault(e.Kind) + 1;
                log.Write(e.Kind, new { at = e.At.ToString("O"), e.Data });
                if (echo)
                {
                    Console.WriteLine($"  {e.At.ToLocalTime():HH:mm:ss.fff} {e.Kind} {System.Text.Json.JsonSerializer.Serialize(e.Data, SpikeLog.JsonOptions)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static object? DescribeSim(WindowHandle sim)
    {
        if (sim.IsNone)
        {
            return null;
        }

        var (cls, children) = WindowMessageTyping.Describe(sim.Value);
        return new { windowClass = cls, children };
    }

    /// <summary>Changes to iRacing's app.ini (where the chat macros are): a program could rewrite macros.</summary>
    private static FileSystemWatcher? WatchAppIni(SpikeLog log)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing");
        if (!Directory.Exists(dir))
        {
            log.Write("app_ini", new { watching = false, reason = "no iRacing documents folder" });
            return null;
        }

        var watcher = new FileSystemWatcher(dir, "app.ini") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, e) => log.Write("app_ini", new { changed = e.ChangeType.ToString() });
        watcher.EnableRaisingEvents = true;
        log.Write("app_ini", new { watching = true });
        return watcher;
    }

    private sealed record Attempt(int Number, string Variant, bool Skipped, bool SimCameForward, string? Leftover);
}
