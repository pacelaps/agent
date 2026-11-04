namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>
/// The iRacing SDK's broadcast messages (irsdk_defines.h): a program sends the registered window message
/// <see cref="MessageName"/> to every top-level window with <c>wParam = MAKELONG(msg, var1)</c> and
/// <c>lParam = var2</c> (or <c>MAKELONG(var2, var3)</c>). Every top-level window receives it, so a listener sees what
/// any program asks the sim to do through the SDK (spike 16). Pure, for the monitor's log.
/// </summary>
public static class SdkBroadcast
{
    public const string MessageName = "IRSDK_BROADCASTMSG";

    private static readonly string[] Messages =
    [
        "CamSwitchPos", "CamSwitchNum", "CamSetState", "ReplaySetPlaySpeed", "ReplaySetPlayPosition", "ReplaySearch",
        "ReplaySetState", "ReloadTextures", "ChatCommand", "PitCommand", "TelemCommand", "FFBCommand",
        "ReplaySearchSessionTime", "VideoCapture",
    ];

    private static readonly string[] ChatModes = ["Macro", "BeginChat", "Reply", "Cancel"];

    /// <summary>The message, its first argument and the raw second, decoded where the SDK defines them.</summary>
    public static (string Message, int Var1, long Var2, string Description) Decode(nint wParam, nint lParam)
    {
        var w = (long)wParam;
        var msg = (int)(w & 0xFFFF);
        var var1 = (int)((w >> 16) & 0xFFFF);
        var var2 = (long)lParam;
        var name = msg >= 0 && msg < Messages.Length ? Messages[msg] : $"Unknown{msg}";
        var description = name switch
        {
            "ChatCommand" when var1 is >= 0 and < 4 => var1 == 0
                // The macro number is zero-based: slot 14 in the sim's options is macro 13 (spike 1).
                ? $"chat macro {var2} (slot {var2 + 1})"
                : $"chat {ChatModes[var1]}",
            "PitCommand" => $"pit command {var1} ({var2})",
            _ => $"{name} {var1} {var2}",
        };
        return (name, var1, var2, description);
    }
}
