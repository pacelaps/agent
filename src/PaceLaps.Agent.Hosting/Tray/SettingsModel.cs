using System.Globalization;
using System.Text.Json;
using PaceLaps.Agent.Core.Config;

namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>
/// The settings screen's fields as the operator types them. <see cref="ToConfig"/> checks them with the settings
/// file's own parser, so the screen names the same errors the file would. Changes apply at the next Activate.
/// </summary>
public sealed class SettingsModel
{
    public const int Slots = 15;

    public string Server { get; set; } = "";

    public string League { get; set; } = "";

    public bool MacrosEnabled { get; set; }

    /// <summary>Slot texts, index 0 is slot 1; empty means unassigned.</summary>
    public string[] MacroSlots { get; } = Enumerable.Repeat("", Slots).ToArray();

    public bool LocalFailsafeEnabled { get; set; }

    public string LocalFailsafeDelay { get; set; } = LocalFailsafeSettings.DefaultDelaySeconds.ToString(CultureInfo.InvariantCulture);

    public string AppIni { get; set; } = "";

    /// <summary>Typed commands without bringing iRacing to the front (spec-feedback 37); on by default.</summary>
    public bool TypedWithoutFocus { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public static SettingsModel From(AgentConfig config, bool startWithWindows)
    {
        var model = new SettingsModel
        {
            Server = config.Server?.ToString() ?? "",
            League = config.League?.ToString(CultureInfo.InvariantCulture) ?? "",
            MacrosEnabled = config.Macros.Enabled,
            LocalFailsafeEnabled = config.LocalFailsafe.Enabled,
            LocalFailsafeDelay = config.LocalFailsafe.DelaySeconds.ToString(CultureInfo.InvariantCulture),
            AppIni = config.AppIni ?? "",
            TypedWithoutFocus = config.TypedWithoutFocus,
            StartWithWindows = startWithWindows,
        };
        foreach (var (slot, text) in config.Macros.Slots)
        {
            if (slot is >= 1 and <= Slots)
            {
                model.MacroSlots[slot - 1] = text;
            }
        }

        return model;
    }

    /// <summary>The settings, or every error in them.</summary>
    public (AgentConfig? Config, IReadOnlyList<string> Errors) ToConfig()
    {
        var doc = new Dictionary<string, object?>
        {
            ["server"] = Blank(Server) ? null : Server.Trim(),
            ["league"] = Blank(League) ? null : Number(League),
            ["macros"] = new Dictionary<string, object?>
            {
                ["enabled"] = MacrosEnabled,
                ["slots"] = MacroSlots.Select((text, i) => (Slot: i + 1, Text: text))
                    .Where(s => !Blank(s.Text))
                    .ToDictionary(s => s.Slot.ToString(CultureInfo.InvariantCulture), s => s.Text.Trim()),
            },
            ["localFailsafe"] = new Dictionary<string, object?>
            {
                ["enabled"] = LocalFailsafeEnabled,
                ["delaySeconds"] = Number(LocalFailsafeDelay),
            },
            ["appIni"] = Blank(AppIni) ? null : AppIni.Trim(),
            ["typedWithoutFocus"] = TypedWithoutFocus,
        };
        var (config, errors) = AgentConfig.Parse(JsonSerializer.Serialize(doc));
        return errors.Count == 0 ? (config, errors) : (null, errors);
    }

    private static bool Blank(string s) => string.IsNullOrWhiteSpace(s);

    /// <summary>A whole number as a number; anything else as text, so the parser names the setting.</summary>
    private static object Number(string s) => long.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : s.Trim();
}
