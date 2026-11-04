namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>A line in a config file that contains the marker, with the section it is in.</summary>
internal sealed record MarkerHit(string File, int Line, string? Section, string Text, IReadOnlyList<string> SectionKeys);

internal sealed record BindingFile(string File, long Bytes, DateTime ModifiedUtc);

/// <summary>
/// Spike 10: can the macro configuration be read? Read-only. The operator puts a marker text into a macro slot;
/// the runner searches iRacing's documents folder for files that contain it and reports where (file, section,
/// key). After the operator changes the marker and restarts the sim, a second run checks the new text is found.
/// It also lists files that look like key-binding files, for the "no plain character keys" check.
/// Only the matching line is logged; other macro texts are reduced to their key names.
/// </summary>
internal static class Spike10MacroConfig
{
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly string[] TextExtensions = [".ini", ".cfg", ".txt", ".json", ".xml", ".yaml", ".yml", ""];

    private static readonly string[] BindingHints = ["control", "joy", "key", "bind", "input"];

    public static Task<int> RunAsync(SpikeEnvironment env)
    {
        var marker = env.Args.Require("marker");
        if (marker.Length < 6)
        {
            throw new Cli.CliException("--marker should be at least 6 characters, so it is unique (for example spike10markerA)");
        }

        var dir = env.Args.Get("dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing");
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Folder not found: {dir}. Pass the iRacing documents folder with --dir.");
            return Task.FromResult(1);
        }

        using var log = env.CreateLog(10);
        log.Write("header", new { spike = 10, runId = log.RunId, agentVersion = SpikeEnvironment.AgentVersion, marker, dir });
        Console.WriteLine($"Log: {log.Path}");
        Prompt.Heading($"Spike 10: searching {dir} for '{marker}' (read-only)");

        var hits = Search(dir, marker);
        foreach (var h in hits)
        {
            log.Write("hit", h);
            Console.WriteLine($"  {h.File}:{h.Line}  [{h.Section ?? "-"}]  {h.Text}");
            Console.WriteLine($"    keys in that section: {string.Join(", ", h.SectionKeys.Take(40))}{(h.SectionKeys.Count > 40 ? ", ..." : "")}");
        }

        if (hits.Count == 0)
        {
            Prompt.Warn("  Marker not found. Has the sim saved the macro? Some settings are only written when the sim closes.");
        }

        var bindings = BindingCandidates(dir);
        foreach (var b in bindings)
        {
            log.Write("bindingCandidate", b);
        }

        Console.WriteLine($"  Key-binding candidates: {(bindings.Count == 0 ? "none" : string.Join(", ", bindings.Select(b => $"{b.File} ({b.Bytes} bytes)")))}");

        var slot = Prompt.Ask("Which macro slot holds the marker (1-15, blank if unknown)?");
        var restarted = Prompt.YesNo("Did you restart the sim since you last changed this macro?");
        var notes = Prompt.Ask("Notes (blank for none):");
        log.Write("summary", new { found = hits.Count > 0, hits = hits.Count, slot, restarted, bindingCandidates = bindings.Count, notes });
        Console.WriteLine($"Found: {hits.Count > 0}. Log: {log.Path}");
        return Task.FromResult(0);
    }

    public static List<MarkerHit> Search(string root, string marker)
    {
        var hits = new List<MarkerHit>();
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes || !TextExtensions.Contains(info.Extension.ToLowerInvariant()))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            hits.AddRange(FindInLines(Path.GetRelativePath(root, file), lines, marker));
        }

        return hits;
    }

    /// <summary>Hits in one file's lines, each with its ini-style section and the key names (not values) of that section.</summary>
    public static IEnumerable<MarkerHit> FindInLines(string file, IReadOnlyList<string> lines, string marker)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var sectionStart = -1;
            for (var j = i; j >= 0; j--)
            {
                if (IsSection(lines[j]))
                {
                    sectionStart = j;
                    break;
                }
            }

            var section = sectionStart >= 0 ? lines[sectionStart].Trim() : null;
            var keys = new List<string>();
            for (var j = sectionStart + 1; j < lines.Count && !IsSection(lines[j]); j++)
            {
                var eq = lines[j].IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    keys.Add(lines[j][..eq].Trim());
                }
            }

            var text = lines[i].Trim();
            yield return new MarkerHit(file, i + 1, section, text.Length > 200 ? text[..200] : text, keys);
        }
    }

    public static List<BindingFile> BindingCandidates(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => BindingHints.Any(h => Path.GetFileName(f).Contains(h, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new FileInfo(f))
            .Select(i => new BindingFile(Path.GetRelativePath(root, i.FullName), i.Length, i.LastWriteTimeUtc))
            .ToList();

    private static bool IsSection(string line)
    {
        var t = line.Trim();
        return t.Length > 2 && t[0] == '[' && t[^1] == ']';
    }
}
