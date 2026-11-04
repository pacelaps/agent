using System.Globalization;
using System.Text.RegularExpressions;
using PaceLaps.Agent.Core.SelfTest;

namespace PaceLaps.Agent.IRacing.Sdk;

/// <summary>
/// Reads the chat macro texts from the sim's app.ini (spike 10): section [Autochat Messages], keys AutoChatStr1 to
/// AutoChatStr15, each value padded and followed by the comment "; Auto chat message". Read-only.
/// </summary>
public sealed partial class AppIniMacroConfig(string path) : IMacroConfigSource
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "app.ini");

    [GeneratedRegex(@"^\s*AutoChatStr(\d{1,2})\s*=(.*)$")]
    private static partial Regex Entry();

    [GeneratedRegex(@"\s*;\s*Auto chat message\s*$")]
    private static partial Regex TrailingComment();

    public string Location => path;

    public IReadOnlyDictionary<int, string>? ReadSlots()
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The slots of the [Autochat Messages] section; null if the section is missing.</summary>
    public static IReadOnlyDictionary<int, string>? Parse(IEnumerable<string> lines)
    {
        Dictionary<int, string>? slots = null;
        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line.Equals("[Autochat Messages]", StringComparison.OrdinalIgnoreCase);
                if (inSection)
                {
                    slots ??= [];
                }

                continue;
            }

            if (!inSection || Entry().Match(raw) is not { Success: true } m)
            {
                continue;
            }

            var slot = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (slot is >= 1 and <= 15)
            {
                slots![slot] = TrailingComment().Replace(m.Groups[2].Value, "").Trim();
            }
        }

        return slots;
    }
}
