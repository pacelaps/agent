namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>Console questions for the operator. Answers go into the spike log.</summary>
internal static class Prompt
{
    public static string Ask(string question)
    {
        Console.Write(question + " ");

        // Null means Ctrl+C or end of input. It must never read as Enter, which sends a command in spike 3.
        return Console.ReadLine()?.Trim() ?? throw new OperationCanceledException("input closed");
    }

    public static bool YesNo(string question)
    {
        while (true)
        {
            var a = Ask(question + " [y/n]").ToLowerInvariant();
            if (a is "y" or "yes" or "j" or "ja")
            {
                return true;
            }

            if (a is "n" or "no" or "nein")
            {
                return false;
            }
        }
    }

    public static int Int(string question, int min, int max)
    {
        while (true)
        {
            if (int.TryParse(Ask($"{question} [{min}-{max}]"), out var v) && v >= min && v <= max)
            {
                return v;
            }
        }
    }

    public static SortedSet<int> NumberList(string question, int max)
    {
        while (true)
        {
            if (Stats.TryParseNumberList(Ask(question), max, out var numbers, out var error))
            {
                return numbers;
            }

            Console.WriteLine("  " + error);
        }
    }

    public static void Heading(string text)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    public static void Warn(string text)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    /// <summary>True if Enter was pressed in the console since the last check. Other keys are discarded.</summary>
    public static bool EnterPressed()
    {
        var pressed = false;
        while (Console.KeyAvailable)
        {
            if (Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
            {
                pressed = true;
            }
        }

        return pressed;
    }

    public static void DrainKeys() => EnterPressed();

    /// <summary>A key the operator pressed, if any; never one when the input is not a console (a background run).</summary>
    public static ConsoleKeyInfo? KeyIfAvailable() =>
        !Console.IsInputRedirected && Console.KeyAvailable ? Console.ReadKey(intercept: true) : null;
}
