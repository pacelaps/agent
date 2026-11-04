using System.Globalization;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>One typed send of spike 15 and what came of it.</summary>
internal sealed record Spike15Send(string Car, string Variant, int Rep, string Text, bool Sent, bool EffectSeen, double? EffectAfterSeconds,
    ReadbackOutcome Readback, string? LeftInChat);

internal sealed record Spike15Case(string Car, string Variant, int Sends, int Effects, int LeftInChat);

/// <summary>
/// Spike 15 (added after the proof-of-concept acceptance run): why did "!eol #7" stay in the chat box with the
/// Enter ignored, twice? And can the agent read the chat box, so it never leaves text there unnoticed?
/// <list type="number">
/// <item>Phase A: can the chat box be read back (begin-chat, Ctrl+A, Ctrl+C through the clipboard)?</item>
/// <item>Phase B (green flag): black flag and clear on the suspect car and a control car, in four forms (plain,
/// 100 ms and 300 ms pause before Enter, trailing space), each followed by a telemetry check and a readback.</item>
/// <item>Phase C (optional, caution): !eol on both cars.</item>
/// </list>
/// </summary>
internal static class Spike15ChatEnter
{
    public static readonly (string Name, TimeSpan BeforeEnter, bool TrailingSpace)[] Variants =
    [
        ("plain", TimeSpan.Zero, false),
        ("pause100", TimeSpan.FromMilliseconds(100), false),
        ("pause300", TimeSpan.FromMilliseconds(300), false),
        ("trailingSpace", TimeSpan.Zero, true),
    ];

    private const uint FlagBlack = 0x00010000;
    private const uint PaceEndOfLine = 0x01;

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var reps = env.Args.GetInt("reps", 2, 1, 10);
        var settle = env.Args.GetMs("settle", TimeSpan.FromMilliseconds(400), 5000);
        if (!await env.ConnectAsync(["SessionFlags", "CarIdxSessionFlags", "CarIdxPaceFlags"]) || !env.PreflightTyping())
        {
            return 1;
        }

        var session = env.Sim.Session!;
        string AskCar(string question, string? fallback)
        {
            while (true)
            {
                var a = Prompt.Ask(question + (fallback is null ? "" : $" [{fallback}]"));
                if (a.Length == 0 && fallback is not null)
                {
                    a = fallback;
                }

                if (Guided.GuidedSteps.IsValidCarNumber(a) && session.CarByNumber(a) is not null)
                {
                    return a;
                }

                Console.WriteLine("  Not a car in this session.");
            }
        }

        var numbers = session.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar).Select(c => c.CarNumber).ToList();
        Prompt.Heading("Spike 15: chat Enter and chat readback");
        Console.WriteLine("Cars: " + string.Join(" ", numbers.Select(n => "#" + n)));
        var suspect = AskCar("Suspect car (its number is the start of another car's number, like #7 with #77):", numbers.Contains("7") ? "7" : null);
        var control = AskCar("Control car (no other car's number starts with it):", null);
        var suspectPrefixOf = PrefixOf(suspect, numbers);
        var controlPrefixOf = PrefixOf(control, numbers);
        if (suspectPrefixOf.Count == 0)
        {
            Prompt.Warn($"  #{suspect} is not the start of another car's number in this session.");
        }

        if (controlPrefixOf.Count > 0)
        {
            Prompt.Warn($"  #{control} is the start of {string.Join(", ", controlPrefixOf.Select(n => "#" + n))}; choose another control car if you can.");
        }

        Prompt.Warn("This spike uses the clipboard: copy anything important elsewhere first. Text on the clipboard is restored afterwards; images and files are not.");

        using var log = env.CreateLog(15);
        log.Write("header", env.Header(15, log.RunId, new { suspect, control, suspectPrefixOf, controlPrefixOf, reps, settleMs = settle.TotalMilliseconds, variants = Variants.Select(v => v.Name) }));
        Console.WriteLine($"Log: {log.Path}");
        var readback = new ChatReadback(env.Focus, env.Keyboard, env.Sim, env.Clock);

        // Phase A: can the chat box be read?
        Prompt.Heading("Phase A: chat readback (no race effect). Keep iRacing's chat visible.");
        Prompt.Ask("  [Enter] start:");
        var probe = "spike15 readback test";
        var unsent = await TypeWithoutEnterAsync(env, probe);
        var a1 = await readback.ReadAndCloseAsync(openChat: true, env.Cancellation);
        Console.WriteLine($"  A1 unsent line, read after begin-chat: {a1.Outcome} {a1.Text}");
        var a1Closed = Prompt.YesNo("  Is the chat box closed and empty now?");
        log.Write("phaseA", new { step = "A1", typed = unsent, outcome = a1.Outcome, text = a1.Text, detail = a1.Detail, chatClosedAndEmpty = a1Closed });

        ReadbackResult? a1b = null;
        if (a1.Outcome != ReadbackOutcome.Text)
        {
            // Maybe begin-chat clears the box: copy straight from the box that is still open.
            await TypeWithoutEnterAsync(env, probe);
            a1b = await readback.ReadAndCloseAsync(openChat: false, env.Cancellation);
            Console.WriteLine($"  A1b unsent line, read without a new begin-chat: {a1b.Outcome} {a1b.Text}");
            log.Write("phaseA", new { step = "A1b", outcome = a1b.Outcome, text = a1b.Text, detail = a1b.Detail, chatClosedAndEmpty = Prompt.YesNo("  Is the chat box closed and empty now?") });
        }

        var a2 = await readback.ReadAndCloseAsync(openChat: true, env.Cancellation);
        Console.WriteLine($"  A2 empty chat box: {a2.Outcome} {a2.Text}");
        log.Write("phaseA", new { step = "A2", outcome = a2.Outcome, text = a2.Text, detail = a2.Detail });
        var readbackWorks = a1.Outcome == ReadbackOutcome.Text && a1.Text == probe;
        var readbackNeedsOpenBox = !readbackWorks && a1b is { Outcome: ReadbackOutcome.Text } && a1b.Text == probe;
        Console.WriteLine(readbackWorks ? "  Readback works." : readbackNeedsOpenBox ? "  Readback works only on a box that is still open (begin-chat clears it)." : "  Readback does not work: watch the chat box yourself in phase B.");

        // Phase B: black flag and clear in four forms on both cars.
        Prompt.Heading($"Phase B: !black and !clear on #{suspect} and #{control}, {Variants.Length} forms, {reps} time(s) each. Green flag, do not touch keyboard or mouse.");
        Prompt.Ask("  [Enter] start:");
        var sends = new List<Spike15Send>();
        foreach (var car in new[] { suspect, control })
        {
            var idx = session.CarByNumber(car)!.CarIdx;
            foreach (var (name, beforeEnter, trailingSpace) in Variants)
            {
                var sender = env.CreateSender(env.TypedChatOptionsFromArgs() with { BeforeEnterDelay = beforeEnter });
                for (var rep = 1; rep <= reps; rep++)
                {
                    foreach (var (text, wantBlack) in new[] { ($"!black #{car} D", true), ($"!clear #{car}", false) })
                    {
                        var send = await SendAndCheckAsync(env, sender, readbackWorks ? readback : null, car, name, rep, trailingSpace ? text + " " : text,
                            settle, f => (Bits(f, "CarIdxSessionFlags", idx) & FlagBlack) != 0 == wantBlack);
                        sends.Add(send);
                        log.Write("send", send);
                        Console.WriteLine($"  #{car} {name,-13} {rep} {text,-14} effect {(send.EffectSeen ? "yes" : "NO"),-3} chat {(send.LeftInChat is null ? (send.Readback == ReadbackOutcome.Failed ? "?" : "empty") : $"LEFT: '{send.LeftInChat}'")}");
                    }
                }
            }
        }

        // Phase C, optional: !eol under caution.
        if (Prompt.YesNo("Phase C: run !eol on both cars under a caution (the runner throws !yellow)?"))
        {
            var sender = env.CreateSender(env.TypedChatOptionsFromArgs());
            await sender.SendAsync("!yellow", env.Cancellation);
            Prompt.Ask("  Caution thrown. Press Enter here once the pace car leads the field:");
            foreach (var car in new[] { suspect, control })
            {
                var idx = session.CarByNumber(car)!.CarIdx;
                var send = await SendAndCheckAsync(env, sender, readbackWorks ? readback : null, car, "eol", 1, $"!eol #{car}", settle,
                    f => (Bits(f, "CarIdxPaceFlags", idx) & PaceEndOfLine) != 0);
                sends.Add(send);
                log.Write("send", send);
                Console.WriteLine($"  #{car} !eol effect {(send.EffectSeen ? "yes" : "NO")} chat {send.LeftInChat ?? send.Readback.ToString()}");
            }
        }

        var cases = Summarize(sends);
        foreach (var c in cases)
        {
            log.Write("case", c);
        }

        var notes = Prompt.Ask("What did you see in chat (left-over lines, iRacing replies)? Blank for none:");
        log.Write("summary", new { readbackWorks, readbackNeedsOpenBox, cases, notes });
        Console.WriteLine();
        foreach (var c in cases)
        {
            Console.WriteLine($"  #{c.Car} {c.Variant,-13} effect {c.Effects}/{c.Sends}, left in chat {c.LeftInChat}");
        }

        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }

    /// <summary>Car numbers in the session that start with this one (and are longer).</summary>
    public static List<string> PrefixOf(string car, IEnumerable<string> numbers) =>
        numbers.Where(n => n.Length > car.Length && n.StartsWith(car, StringComparison.Ordinal)).Distinct().ToList();

    public static List<Spike15Case> Summarize(IEnumerable<Spike15Send> sends) =>
        sends.GroupBy(s => (s.Car, s.Variant))
            .Select(g => new Spike15Case(g.Key.Car, g.Key.Variant, g.Count(), g.Count(s => s.EffectSeen), g.Count(s => s.LeftInChat is not null)))
            .ToList();

    private static async Task<Spike15Send> SendAndCheckAsync(SpikeEnvironment env, TypedChatSender sender, ChatReadback? readback,
        string car, string variant, int rep, string text, TimeSpan settle, Func<PaceLaps.Agent.IRacing.Sdk.TelemetryFrame, bool> effect)
    {
        var result = await sender.SendAsync(text, env.Cancellation);
        var sentAt = env.Clock.Elapsed;
        double? after = null;
        while (env.Clock.Elapsed - sentAt < TimeSpan.FromSeconds(3))
        {
            if (env.Sim.Latest is { } f && effect(f))
            {
                after = Math.Round((env.Clock.Elapsed - sentAt).TotalSeconds, 2);
                break;
            }

            await env.Clock.Delay(TimeSpan.FromMilliseconds(50), env.Cancellation);
        }

        await env.Clock.Delay(settle, env.Cancellation);
        var rb = readback is null ? new ReadbackResult(ReadbackOutcome.Failed, null, "readback not available") : await readback.ReadAndCloseAsync(true, env.Cancellation);
        return new Spike15Send(car, variant, rep, text, result.Success, after is not null, after, rb.Outcome, rb.Outcome == ReadbackOutcome.Text ? rb.Text : null);
    }

    /// <summary>Begin chat and type a line, without Enter. Returns the text, or null if it could not be typed.</summary>
    private static async Task<string?> TypeWithoutEnterAsync(SpikeEnvironment env, string text)
    {
        var sim = env.Focus.FindSimWindow();
        env.Focus.BringToFront(sim);
        await env.Clock.Delay(TimeSpan.FromMilliseconds(200), env.Cancellation);
        if (!env.Keyboard.TryMap(text, out var strokes, out _))
        {
            return null;
        }

        env.Sim.BeginChat();
        await env.Clock.Delay(TimeSpan.FromMilliseconds(150), env.Cancellation);
        env.Keyboard.Send(strokes);
        await env.Clock.Delay(TimeSpan.FromMilliseconds(200), env.Cancellation);
        return text;
    }

    private static uint Bits(PaceLaps.Agent.IRacing.Sdk.TelemetryFrame f, string name, int idx) =>
        f.Get(name, idx) is { } v ? Convert.ToUInt32(v, CultureInfo.InvariantCulture) : 0u;
}
