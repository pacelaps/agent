using PaceLaps.Agent.Core.TypedChat;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>
/// The typed channel's way without the focus (spike 16, spec-feedback 37): WM_CHAR per character and Enter as
/// WM_KEYDOWN/UP, posted to the sim's window. Posted, not sent: IRSDKSharper posts the SDK's begin and cancel chat
/// too, so the sim handles them in order. Characters go as Unicode, so the keyboard layout does not matter.
/// </summary>
public sealed class PostedChatInput : IChatPoster
{
    public bool PostText(WindowHandle window, string text) =>
        WindowMessageTyping.PostText(window.Value, text, MessageDelivery.Post).Failed == 0;

    public bool PostEnter(WindowHandle window) =>
        WindowMessageTyping.PostEnter(window.Value, PostedEnter.Key, MessageDelivery.Post).Failed == 0;
}
