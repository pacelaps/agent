using System.Globalization;
using PaceLaps.Agent.Core.TypedChat;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

internal sealed record Spike2Summary(
    string Mode,
    int Count,
    int Sent,
    int ReportedMissing,
    int Arrived,
    double MedianMs,
    double P95Ms,
    double MaxMs,
    int FocusRestored,
    string Result,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Spike 2: how reliable is typed chat? Sends numbered /rc messages through <see cref="TypedChatSender"/> while
/// another window has focus, measures each send, then asks the operator which numbers never showed up.
/// </summary>
internal static class Spike2TypedChat
{
    public const int MinArrived = 49;
    public const int OfCount = 50;
    public const double MaxMedianMs = 500;

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var mode = env.Args.GetChoice("mode", "borderless", "fullscreen");
        var count = env.Args.GetInt("count", OfCount, 1, 500);
        var interval = env.Args.GetSeconds("interval", TimeSpan.FromSeconds(2), 60);
        var startDelay = env.Args.GetSeconds("start-delay", TimeSpan.FromSeconds(10), 120);
        // "none" sends plain chat; in an AI session nobody else reads it.
        var prefix = env.Args.Get("prefix") ?? "/rc";
        if (prefix == "none")
        {
            prefix = "";
        }
        var options = env.TypedChatOptionsFromArgs();

        if (!await env.ConnectAsync([]) || !env.PreflightTyping())
        {
            return 1;
        }

        using var log = env.CreateLog(2);
        log.Write("header", env.Header(2, log.RunId, new
        {
            mode,
            count,
            intervalSeconds = interval.TotalSeconds,
            prefix,
            chatOpenDelayMs = Stats.Ms(options.ChatOpenDelay),
            keyDelayMs = Stats.Ms(options.InterKeyDelay),
            afterEnterDelayMs = Stats.Ms(options.AfterEnterDelay),
            focusTimeoutMs = Stats.Ms(options.FocusTimeout),
        }));
        Console.WriteLine($"Log: {log.Path}");

        var hint = env.Focus.DescribeSimWindow()?.ModeHint;
        if (hint == "windowed")
        {
            Prompt.Warn($"The iRacing window has a title bar (windowed), but --mode is {mode}. Fix the window mode or the flag.");
            if (!Prompt.YesNo("Continue anyway?"))
            {
                return 1;
            }
        }

        var sender = env.CreateSender(options);
        Prompt.Heading($"Spike 2: {count} typed messages, {mode}, every {interval.TotalSeconds:F0} s");

        // Verify the text can be typed on this layout before anything is sent.
        var sample = Text(prefix, mode, count, count, log.RunId);
        if (!env.Keyboard.TryMap(sample, out _, out var bad))
        {
            Console.Error.WriteLine($"'{bad}' cannot be typed on keyboard layout {env.Focus.SimKeyboardLayout() & 0xFFFF:X4}. Use --prefix to change the text.");
            return 1;
        }

        Console.WriteLine($"Messages look like:  {sample}");
        await env.CountdownAsync(startDelay,
            "Click into your browser now. Keep iRacing's chat visible if you can. Do not touch keyboard or mouse until it is done.");

        var results = new List<TypedChatResult>(count);
        for (var i = 1; i <= count; i++)
        {
            var text = Text(prefix, mode, i, count, log.RunId);
            var result = await sender.SendAsync(text, env.Cancellation);
            results.Add(result);
            var after = env.Focus.DescribeProcess(env.Focus.GetForeground());
            log.Write("message", new
            {
                i,
                text,
                success = result.Success,
                failure = result.Failure,
                detail = result.Detail,
                foregroundBefore = result.PreviousProcess,
                foregroundAfter = after,
                simWasForeground = result.SimWasForeground,
                focusMethod = result.FocusMethod,
                focusRestored = result.FocusRestored,
                chatCancelled = result.ChatCancelled,
                focusMs = Stats.Ms(result.FocusTime),
                chatOpenMs = Stats.Ms(result.ChatOpenTime),
                typingMs = Stats.Ms(result.TypingTime),
                restoreMs = Stats.Ms(result.RestoreTime),
                totalMs = Stats.Ms(result.TotalTime),
            });

            Console.WriteLine(result.Success
                ? $"  {i:00}/{count} sent  {Stats.Ms(result.TotalTime),6:F0} ms  focus {(result.FocusRestored ? "restored" : "NOT restored")}  ({result.PreviousProcess} -> {after})"
                : $"  {i:00}/{count} FAILED {result.Failure}: {result.Detail}");

            if (result.SimWasForeground)
            {
                Prompt.Warn("        iRacing was already in front: this message does not test focus handling. Click your browser.");
            }

            if (i < count)
            {
                await env.Clock.Delay(interval, env.Cancellation);
            }
        }

        Console.Beep();
        Prompt.Heading("Done. Answer from what you saw in iRacing chat:");
        var missing = Prompt.NumberList($"Which message numbers did NOT appear? (e.g. 3, 7-9; blank = all appeared)", count);
        var garbled = Prompt.NumberList("Which appeared garbled or cut off? (blank = none)", count);
        var chatLeftOpen = Prompt.YesNo("Was the chat box ever left open with text in it?");
        var notes = Prompt.Ask("Notes (blank for none):");

        var summary = Evaluate(mode, results, missing.Union(garbled).ToHashSet());
        log.Write("operator", new { missing, garbled, chatLeftOpen, notes });
        log.Write("summary", summary);

        Console.WriteLine();
        Console.WriteLine($"Result: {summary.Result}. Arrived {summary.Arrived}/{summary.Count}, median {summary.MedianMs:F0} ms, p95 {summary.P95Ms:F0} ms, "
            + $"max {summary.MaxMs:F0} ms, focus restored {summary.FocusRestored}/{summary.Count}.");
        foreach (var r in summary.Reasons)
        {
            Console.WriteLine("  - " + r);
        }

        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }

    public static string Text(string prefix, string mode, int i, int count, string runId) =>
        string.Create(CultureInfo.InvariantCulture, $"{prefix} s2 {(mode == "borderless" ? "bw" : "fs")} {i:00}/{count} {runId}").TrimStart();

    /// <summary>
    /// Applies the spec's pass criteria (borderless only): at least 49 of 50 arrive, median under 500 ms,
    /// focus restored every time. A message counts as arrived if it was sent and the operator did not report it
    /// missing or garbled. Fullscreen is recorded without a verdict.
    /// </summary>
    public static Spike2Summary Evaluate(string mode, IReadOnlyList<TypedChatResult> results, IReadOnlySet<int> notArrived)
    {
        var sent = results.Count(r => r.Success);
        var arrived = results.Where((r, idx) => r.Success && !notArrived.Contains(idx + 1)).Count();
        var times = results.Where(r => r.Success).Select(r => r.TotalTime.TotalMilliseconds).ToArray();
        var median = Stats.Median(times);
        var restored = results.Count(r => r.FocusRestored);
        var tested = results.Count(r => !r.SimWasForeground);

        var reasons = new List<string>();
        var required = (int)Math.Ceiling(results.Count * (double)MinArrived / OfCount);
        if (arrived < required)
        {
            reasons.Add($"only {arrived} of {results.Count} arrived (need {required})");
        }

        if (!(median < MaxMedianMs))
        {
            reasons.Add($"median {median:F0} ms is not under {MaxMedianMs:F0} ms");
        }

        if (restored < results.Count)
        {
            reasons.Add($"focus not restored {results.Count - restored} time(s)");
        }

        if (tested < results.Count)
        {
            reasons.Add($"{results.Count - tested} message(s) sent while iRacing was already in front (run is not a clean test)");
        }

        var result = mode != "borderless" ? "recorded" : reasons.Count == 0 ? "pass" : "fail";
        return new Spike2Summary(mode, results.Count, sent, results.Count(r => r.Success) - arrived, arrived, Math.Round(median, 1),
            Math.Round(Stats.Percentile(times, 95), 1), times.Length == 0 ? double.NaN : Math.Round(times.Max(), 1), restored, result, reasons);
    }
}
