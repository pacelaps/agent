using System.Globalization;
using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes.Guided;

/// <summary>
/// Walks the operator through a steps file (spikes 3, 9, 12, 13, 14): sends each command through the typed
/// channel, records telemetry for the whole run, checks expected effects where telemetry can show them,
/// and logs the operator's verdict per step and the answers to the plan's final questions.
/// </summary>
internal static class GuidedRunner
{
    public static async Task<int> RunAsync(SpikeEnvironment env, int defaultSpike, string? defaultSteps)
    {
        var spike = env.Args.GetInt("spike", defaultSpike, 1, 99);
        var stepsPath = GuidedSteps.Resolve(env.Args.Get("steps") ?? defaultSteps
            ?? throw new CliException("--steps is required (a steps file, for example spike9-steps.json)"));
        var plan = GuidedSteps.Load(stepsPath);
        var car = env.Args.Get("car");
        var car2 = env.Args.Get("car2");
        if ((car is not null && !GuidedSteps.IsValidCarNumber(car)) || (car2 is not null && !GuidedSteps.IsValidCarNumber(car2)))
        {
            throw new CliException("--car and --car2 must be car numbers of one to three digits, as shown in the sim");
        }

        var stayInSim = !env.Args.Has("no-stay-in-sim");
        var options = env.TypedChatOptionsFromArgs();

        if (!await env.ConnectAsync(TelemetryDiff.DiscreteVars.Concat(TelemetryDiff.SampledVars)) || !env.PreflightTyping())
        {
            return 1;
        }

        if (car is not null && env.Sim.Session!.CarByNumber(car) is null)
        {
            Console.Error.WriteLine($"Car #{car} is not in this session. Cars: {CarList(env.Sim.Session!)}");
            return 1;
        }

        if (car is null && plan.Steps.Any(s => s.UsesCar))
        {
            car = AskCar("Target car number for this run (a car in the middle of the field):", env);
        }

        using var log = env.CreateLog(spike);
        var session = env.Sim.Session!;
        log.Write("header", env.Header(spike, log.RunId, new
        {
            steps = Path.GetFullPath(stepsPath),
            car,
            car2,
            stayInSim,
            chatOpenDelayMs = Stats.Ms(options.ChatOpenDelay),
            keyDelayMs = Stats.Ms(options.InterKeyDelay),
            about = plan.About,
            plan = plan.Steps,
            plan.FinalQuestions,
        }));
        log.Write("cars", session.Cars.Where(c => !c.IsSpectator).Select(c => new { idx = c.CarIdx, num = c.CarNumber, classId = c.ClassId, paceCar = c.IsPaceCar }).ToArray());
        Console.WriteLine($"Log: {log.Path}");

        using var recording = CancellationTokenSource.CreateLinkedTokenSource(env.Cancellation);
        var recorder = new GuidedRecorder(env.Sim, log, env.Clock, () => car, () => car2);
        var recorderTask = recorder.RunAsync(recording.Token);

        var sender = env.CreateSender(options);
        Prompt.Heading($"Spike {spike}: {plan.Steps.Count} steps{(car is null ? "" : $", target car #{car}")}{(car2 is null ? "" : $", second car #{car2}")}");
        if (plan.About is { Length: > 0 } about)
        {
            Console.WriteLine(about);
        }

        var auto = env.Args.Has("auto");
        if (auto)
        {
            if (car2 is null && plan.Steps.Any(s => s.UsesCar2))
            {
                car2 = AskCar("Number of the second car (asked now, because auto mode does not stop):", env);
            }

            Console.WriteLine("Auto mode: the steps run one after another without prompts. A beep comes 3 s before each command;");
            Console.WriteLine("armed steps wait for their condition. Verdicts and notes are asked at the end, so keep a note of what you hear and see.");
            await env.CountdownAsync(TimeSpan.FromSeconds(15), "Get ready in the car.");
        }
        else
        {
            Console.WriteLine("Use 'e' at a step to type a corrected command; it is logged. After each command iRacing stays in front;");
            Console.WriteLine("switch back here and press Enter to end the watch early. Telemetry is recorded the whole time.");
        }

        var verdicts = new List<object>();
        var pending = new List<(GuidedStep Step, string Text, bool Edited, StepOutcome Outcome)>();
        int documented = 0, noEffect = 0, different = 0, skipped = 0;

        void AskVerdict(GuidedStep step, string text, bool edited, StepOutcome outcome)
        {
            Prompt.Heading($"  {step.Id}{(step.WatchOnly ? " (watch only)" : $": {text}")}");
            foreach (var e in outcome.Expectations)
            {
                Console.WriteLine($"  telemetry {e.Kind}: {e.Outcome}{(e.FirstSeenAfterSeconds is { } sec ? $" after {sec:F1} s" : "")}");
            }

            string verdict;
            while (true)
            {
                verdict = Prompt.Ask("  Effect? [d] as documented/expected, [n] no effect, [o] other/different:").ToLowerInvariant();
                if (verdict is "d" or "n" or "o")
                {
                    break;
                }
            }

            var note = Prompt.Ask("  Note (what happened; blank for none):");
            switch (verdict)
            {
                case "d": documented++; break;
                case "n": noEffect++; break;
                default: different++; break;
            }

            var entry = new
            {
                step = step.Id,
                text,
                edited,
                optional = step.Optional,
                watchOnly = step.WatchOnly,
                auto,
                sent = outcome.Sent?.Success,
                sendFailure = outcome.Sent?.Failure,
                sendMs = outcome.Sent is null ? (double?)null : Stats.Ms(outcome.Sent.TotalTime),
                watchedSeconds = outcome.WatchedSeconds,
                expectations = outcome.Expectations.Select(e => new { kind = e.Kind, outcome = e.Outcome, afterSeconds = e.FirstSeenAfterSeconds }).ToArray(),
                verdict = verdict switch { "d" => "documented", "n" => "noEffect", _ => "different" },
                note,
            };
            verdicts.Add(entry);
            log.Write("step", entry);
        }

        try
        {
            for (var n = 0; n < plan.Steps.Count; n++)
            {
                var step = plan.Steps[n];
                Prompt.Heading($"[{n + 1}/{plan.Steps.Count}] {step.Id}{(step.Optional ? " (optional)" : "")}");
                if (step.UsesCar2 && car2 is null)
                {
                    car2 = AskCar("Number of the second car for this step (for example a lapped car):", env);
                }

                var text = step.Render(car, car2);
                Console.WriteLine(step.WatchOnly ? "  Command:  (none, watch only)" : $"  Command:  {text}");
                if (step.When.Length > 0)
                {
                    Console.WriteLine($"  When:     {step.When}");
                }

                if (step.Observe.Length > 0)
                {
                    Console.WriteLine($"  Observe:  {step.Observe}");
                }

                Console.WriteLine($"  Now:      {StateLine(env.Sim.Latest!, env.Sim.Session!, car)}");
                if (text.StartsWith("!restart", StringComparison.Ordinal) && Expectations.Holds("oneLapToGreen", env.Sim.Latest!, Targets(env, step, car, car2)) == true)
                {
                    Prompt.Warn("  One lap to green is already shown: in spike 3 a restart command sent now did not apply to this restart.");
                }

                var choice = "";
                if (!auto)
                {
                    var question = step.WatchOnly
                        ? "  [Enter] start watching, [s] skip, [q] quit:"
                        : step.SendWhen is null
                            ? "  [Enter] send, [e] edit command, [s] skip, [q] quit:"
                            : $"  [Enter] arm (sends when telemetry shows {step.SendWhen}), [e] edit command, [s] skip, [q] quit:";
                    choice = Prompt.Ask(question).ToLowerInvariant();
                }

                if (choice == "q")
                {
                    log.Write("quit", new { step = step.Id });
                    break;
                }

                if (choice == "e" && !step.WatchOnly)
                {
                    text = Prompt.Ask("  Command text:");
                }

                if (choice == "s" || (text.Length == 0 && !step.WatchOnly))
                {
                    skipped++;
                    log.Write("step", new { step = step.Id, skipped = true });
                    continue;
                }

                if (step.SendWhen is { } condition)
                {
                    var armed = await WaitForConditionAsync(env, step, condition, Targets(env, step, car, car2));
                    log.Write("armed", new { step = step.Id, condition, outcome = armed });
                    if (armed == "skip")
                    {
                        skipped++;
                        log.Write("step", new { step = step.Id, skipped = true });
                        continue;
                    }

                    if (armed == "quit")
                    {
                        log.Write("quit", new { step = step.Id });
                        break;
                    }
                }
                else if (auto && !step.WatchOnly)
                {
                    Console.Beep(880, 300);
                    await env.Clock.Delay(TimeSpan.FromSeconds(3), env.Cancellation);
                }

                var outcome = await RunStepAsync(env, log, recorder, sender, step, text, Targets(env, step, car, car2), stayInSim);
                if (auto)
                {
                    pending.Add((step, text, false, outcome));
                    continue;
                }

                Console.Beep();
                Prompt.Heading($"  {step.Id}: switch back here.");
                AskVerdict(step, text, choice == "e", outcome);
            }

            if (pending.Count > 0)
            {
                Console.Beep();
                Prompt.Heading("All steps done. Switch back here for the verdicts.");
                foreach (var p in pending)
                {
                    AskVerdict(p.Step, p.Text, p.Edited, p.Outcome);
                }
            }

            Prompt.Heading("Final questions");
            var answers = new List<object>();
            var allYes = true;
            foreach (var q in plan.FinalQuestions)
            {
                var yes = Prompt.YesNo(q);
                allYes &= yes;
                answers.Add(new { question = q, yes });
            }

            var notes = Prompt.Ask("Anything else (blank for none):");
            var result = noEffect == 0 && different == 0 && allYes && documented > 0 ? "pass" : documented > 0 ? "partial" : "fail";
            log.Write("summary", new { resultHint = result, documented, noEffect, different, skipped, answers, notes, steps = verdicts });

            Console.WriteLine();
            Console.WriteLine($"Operator summary: {result}. Documented {documented}, no effect {noEffect}, different {different}, skipped {skipped}.");
            Console.WriteLine($"Log: {log.Path}");
            return 0;
        }
        finally
        {
            await recording.CancelAsync();
            await recorderTask;
        }
    }

    /// <param name="Sent">Null for a watch-only step.</param>
    private sealed record StepOutcome(TypedChatResult? Sent, double WatchedSeconds, IReadOnlyList<ExpectationTracker> Expectations);

    private static async Task<StepOutcome> RunStepAsync(SpikeEnvironment env, SpikeLog log, GuidedRecorder recorder,
        TypedChatSender sender, GuidedStep step, string text, ExpectationTargets targets, bool stayInSim)
    {
        var session = env.Sim.Session!;
        var baseline = env.Sim.Latest!;
        log.Write("baseline", new { step = step.Id, targetCarIdx = targets.CarIdx, frame = Snapshot(baseline, session) });

        var trackers = step.Expect.Select(k => new ExpectationTracker(k, baseline, targets)).ToList();
        TypedChatResult? sent = null;
        if (!step.WatchOnly)
        {
            sent = await sender.SendAsync(text, env.Cancellation);
            log.Write("sent", new
            {
                step = step.Id,
                text,
                success = sent.Success,
                failure = sent.Failure,
                detail = sent.Detail,
                focusRestored = sent.FocusRestored,
                totalMs = Stats.Ms(sent.TotalTime),
            });

            if (!sent.Success)
            {
                Prompt.Warn($"  Send failed: {sent.Failure} {sent.Detail}");
                return new StepOutcome(sent, 0, trackers);
            }
        }
        else
        {
            log.Write("watchStart", new { step = step.Id });
        }

        var sentAt = env.Clock.Elapsed;

        recorder.BeginWatch(step.Id, sentAt, trackers);
        try
        {
            if (stayInSim)
            {
                env.Focus.BringToFront(env.Focus.FindSimWindow());
            }

            Console.WriteLine($"  {(step.WatchOnly ? "Watching" : "Sent. Watching")} up to {step.WatchSeconds} s (Enter here ends it).");
            Prompt.DrainKeys();
            var watch = TimeSpan.FromSeconds(step.WatchSeconds);
            while (env.Clock.Elapsed - sentAt < watch)
            {
                await env.Clock.Delay(TimeSpan.FromMilliseconds(100), env.Cancellation);
                if (Prompt.EnterPressed())
                {
                    break;
                }

                if (step.EndWhen is { } end && Expectations.Holds(end, env.Sim.Latest!, targets) == true)
                {
                    Console.WriteLine($"  {end} seen: watch ends in 5 s.");
                    await env.Clock.Delay(TimeSpan.FromSeconds(5), env.Cancellation);
                    break;
                }
            }
        }
        finally
        {
            recorder.EndWatch();
        }

        return new StepOutcome(sent, Math.Round((env.Clock.Elapsed - sentAt).TotalSeconds, 1), trackers);
    }

    /// <summary>Waits until telemetry shows the condition. Returns "condition", "enter", "skip" or "quit".</summary>
    private static async Task<string> WaitForConditionAsync(SpikeEnvironment env, GuidedStep step, string condition, ExpectationTargets targets)
    {
        Console.WriteLine($"  Armed: waiting until telemetry shows {condition}. [Enter] send now, [s] skip, [q] quit.");
        Prompt.DrainKeys();
        while (true)
        {
            if (Expectations.Holds(condition, env.Sim.Latest!, targets) == true)
            {
                Console.WriteLine($"  {condition} seen: sending {step.Id}.");
                return "condition";
            }

            if (Prompt.KeyIfAvailable() is { } key)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter: return "enter";
                    case ConsoleKey.S: return "skip";
                    case ConsoleKey.Q: return "quit";
                }
            }

            await env.Clock.Delay(TimeSpan.FromMilliseconds(50), env.Cancellation);
        }
    }

    private static ExpectationTargets Targets(SpikeEnvironment env, GuidedStep step, string? car, string? car2)
    {
        var session = env.Sim.Session!;
        var number = step.Target == "car2" ? car2 : car;
        return new ExpectationTargets(number is null ? null : session.CarByNumber(number)?.CarIdx, session.PaceCarIdx);
    }

    private static object Snapshot(TelemetryFrame f, SessionInfoView session)
    {
        var scalars = TelemetryDiff.DiscreteVars.Where(v => !v.StartsWith("CarIdx", StringComparison.Ordinal))
            .Where(f.Values.ContainsKey)
            .ToDictionary(v => v, v => (object?)TelemetryDiff.Describe(v, f.Get(v)));
        var cars = session.Cars.Where(c => !c.IsSpectator).Select(c => new
        {
            idx = c.CarIdx,
            num = c.CarNumber,
            values = TelemetryDiff.DiscreteVars.Concat(TelemetryDiff.SampledVars)
                .Where(v => v.StartsWith("CarIdx", StringComparison.Ordinal) && f.Values.ContainsKey(v))
                .ToDictionary(v => v, v => f.Get(v, c.CarIdx)),
        }).ToArray();
        return new { tick = f.TickCount, sessionTime = Math.Round(f.SessionTime, 2), scalars, cars };
    }

    private static string StateLine(TelemetryFrame f, SessionInfoView s, string? car)
    {
        var idx = car is null ? null : s.CarByNumber(car)?.CarIdx;
        string Car(string v) => idx is { } i ? TelemetryDiff.Describe(v, f.Get(v, i)) : "?";
        var line = string.Create(CultureInfo.InvariantCulture,
            $"flags {TelemetryDiff.Describe("SessionFlags", f.Get("SessionFlags"))}, pace mode {TelemetryDiff.Describe("PaceMode", f.Get("PaceMode"))}");
        return car is null ? line : line + $", #{car}: {Car("CarIdxSessionFlags")} / pace {Car("CarIdxPaceFlags")}";
    }

    private static string AskCar(string question, SpikeEnvironment env)
    {
        while (true)
        {
            var a = Prompt.Ask("  " + question);
            if (GuidedSteps.IsValidCarNumber(a) && env.Sim.Session!.CarByNumber(a) is not null)
            {
                return a;
            }

            Console.WriteLine($"  Not a car in this session. Cars: {CarList(env.Sim.Session!)}");
        }
    }

    private static string CarList(SessionInfoView s) =>
        string.Join(" ", s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar).Select(c => "#" + c.CarNumber));
}
