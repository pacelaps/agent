namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Spike 1: do SDK chat macros run while iRacing is not focused? Triggers one macro slot N times with another
/// window in front. The SDK cannot read chat, so the operator counts the messages and the answer is logged.
/// </summary>
internal static class Spike1Macro
{
    public const string MacroText = "/rc spike1 macro test$";

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var slot = env.Args.GetInt("slot", 0, 1, 15);
        if (slot == 0)
        {
            throw new Cli.CliException("--slot is required (the chat macro slot you set up, 1 to 15)");
        }

        var count = env.Args.GetInt("count", 20, 1, 200);
        var interval = env.Args.GetSeconds("interval", TimeSpan.FromSeconds(3), 60);
        var startDelay = env.Args.GetSeconds("start-delay", TimeSpan.FromSeconds(10), 120);

        // Only recorded: the operator types the macro text into the sim. Override if /rc turns out to be wrong.
        var macroText = env.Args.Get("macro-text") ?? MacroText;

        if (!await env.ConnectAsync([]))
        {
            return 1;
        }

        using var log = env.CreateLog(1);
        log.Write("header", env.Header(1, log.RunId, new { slot, count, intervalSeconds = interval.TotalSeconds, macroText }));
        Console.WriteLine($"Log: {log.Path}");

        Prompt.Heading($"Spike 1: macro slot {slot}, {count} triggers, every {interval.TotalSeconds:F0} s");
        Console.WriteLine($"Macro slot {slot} must contain exactly:  {macroText}");

        // Control: with iRacing in front, the macro must work at all, or the unfocused result means nothing.
        if (Prompt.YesNo("Run one control trigger with iRacing in front first?"))
        {
            var sim = env.Focus.FindSimWindow();
            var before = env.Focus.GetForeground();
            env.Focus.BringToFront(sim);
            await env.Clock.Delay(TimeSpan.FromMilliseconds(300), env.Cancellation);
            var simInFront = env.Focus.GetForeground() == sim;
            env.Sim.TriggerMacro(slot);
            await env.Clock.Delay(TimeSpan.FromSeconds(2), env.Cancellation);
            env.Focus.BringToFront(before);
            var seen = Prompt.YesNo("Did 'spike1 macro test' appear in iRacing chat?");
            log.Write("control", new { simInFront, seen });
            if (!seen)
            {
                Prompt.Warn("The macro does not work even with iRacing focused. Check the slot number and text, then run again.");
                log.Write("summary", new { result = "invalid", reason = "control trigger not seen" });
                return 1;
            }
        }

        await env.CountdownAsync(startDelay,
            "Click into your browser (or any window that is not iRacing) and keep iRacing's chat area visible. Do not touch keyboard or mouse.");

        var sims = env.Focus.FindSimWindow();
        int triggered = 0, skipped = 0, simCameForward = 0;
        for (var i = 1; i <= count; i++)
        {
            var fg = env.Focus.GetForeground();
            var fgProcess = env.Focus.DescribeProcess(fg);
            if (fg == sims)
            {
                skipped++;
                log.Write("trigger", new { i, triggered = false, reason = "iRacing was in front", foreground = fgProcess });
                Console.WriteLine($"  {i:00}/{count} skipped: iRacing is in front. Click another window.");
            }
            else
            {
                env.Sim.TriggerMacro(slot);
                triggered++;
                await env.Clock.Delay(TimeSpan.FromMilliseconds(500), env.Cancellation);
                var after = env.Focus.GetForeground();
                var cameForward = after == sims;
                if (cameForward)
                {
                    simCameForward++;
                }

                log.Write("trigger", new { i, triggered = true, foreground = fgProcess, foregroundAfter = env.Focus.DescribeProcess(after), simCameForward = cameForward });
                Console.WriteLine($"  {i:00}/{count} triggered (front: {fgProcess}{(cameForward ? ", iRacing came to front!" : "")})");
            }

            if (i < count)
            {
                await env.Clock.Delay(interval - TimeSpan.FromMilliseconds(500), env.Cancellation);
            }
        }

        Console.Beep();
        Prompt.Heading("Done. Switch back to this window and answer:");
        var seenCount = Prompt.Int($"How many 'spike1 macro test' messages appeared in iRacing chat (of {triggered} triggered)?", 0, count);
        var chatLeftOpen = Prompt.YesNo("Was the iRacing chat box left open (text waiting, not sent) at any point?");
        var notes = Prompt.Ask("Notes (blank for none):");

        var valid = skipped == 0;
        var pass = valid && seenCount == triggered && simCameForward == 0 && !chatLeftOpen;
        var summary = new
        {
            result = !valid ? "invalid" : pass ? "pass" : "fail",
            count,
            triggered,
            skipped,
            seen = seenCount,
            simCameForward,
            chatLeftOpen,
            notes,
        };
        log.Write("summary", summary);

        Console.WriteLine();
        Console.WriteLine($"Result: {summary.result}. Triggered {triggered}, seen {seenCount}, skipped {skipped}, iRacing came forward {simCameForward}x.");
        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }
}
