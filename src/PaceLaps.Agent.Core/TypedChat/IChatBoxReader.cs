namespace PaceLaps.Agent.Core.TypedChat;

/// <param name="Read">False when the chat box could not be read (for example the clipboard was busy).</param>
/// <param name="Text">The chat box text; null when the box is empty.</param>
public sealed record ChatBoxContent(bool Read, string? Text, string? Detail = null)
{
    public static ChatBoxContent Empty { get; } = new(true, null);

    public static ChatBoxContent Unreadable(string detail) => new(false, null, detail);
}

/// <summary>
/// Reads iRacing's chat box (spike 15: begin-chat keeps text already in the box; select-all and copy work).
/// Called with iRacing in front. It leaves the chat box open, so the caller decides: press Enter, or cancel chat.
/// </summary>
public interface IChatBoxReader
{
    Task<ChatBoxContent> PeekAsync(CancellationToken ct);
}

/// <summary>What the check after Enter found.</summary>
public enum ChatBoxCheck
{
    /// <summary>No chat box reader configured.</summary>
    Off,

    /// <summary>The chat box was empty: the line was sent.</summary>
    Empty,

    /// <summary>The line was still in the box; a second Enter sent it.</summary>
    SentOnRetry,

    /// <summary>The line was still in the box after a second Enter; it was cleared and not sent.</summary>
    NotSent,

    /// <summary>The box held other text; it was cleared. Sent if our line was not part of it, failed if it was.</summary>
    UnexpectedText,

    /// <summary>The box could not be read; chat was cancelled as a precaution.</summary>
    Unreadable,
}
