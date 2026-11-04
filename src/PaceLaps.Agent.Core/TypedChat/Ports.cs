namespace PaceLaps.Agent.Core.TypedChat;

/// <summary>An opaque top-level window handle. <see cref="None"/> means no window.</summary>
public readonly record struct WindowHandle(nint Value)
{
    public static readonly WindowHandle None = new(0);

    public bool IsNone => Value == 0;

    public override string ToString() => $"0x{Value:X}";
}

/// <summary>Modifier keys held while a key is pressed. AltGr is sent as Ctrl+Right Alt.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>One character as a hardware scancode press with modifiers.</summary>
public readonly record struct KeyStroke(ushort ScanCode, bool Extended, KeyModifiers Modifiers, char Character);

/// <summary>Window focus operations. Implemented with Win32 in the IRacing project.</summary>
public interface IWindowFocus
{
    WindowHandle GetForeground();

    /// <summary>The iRacing simulator's main window, or <see cref="WindowHandle.None"/> if the sim is not running.</summary>
    WindowHandle FindSimWindow();

    /// <summary>Asks Windows to bring the window to the front. Returns the method that was tried last.</summary>
    string BringToFront(WindowHandle window);

    bool IsWindow(WindowHandle window);

    /// <summary>The executable name of the window's process, for logs. Never the window title (it can hold personal data).</summary>
    string DescribeProcess(WindowHandle window);
}

/// <summary>Scancode keyboard input. Implemented with SendInput in the IRacing project.</summary>
public interface IKeyboard
{
    /// <summary>
    /// Maps text to keystrokes with the keyboard layout the sim will use. Fails on any character
    /// that cannot be typed as plain key presses (control characters, dead keys, characters not on the layout).
    /// </summary>
    bool TryMap(string text, out IReadOnlyList<KeyStroke> strokes, out char unmappable);

    /// <summary>Sends the strokes. Returns false if Windows did not accept all input events.</summary>
    bool Send(IReadOnlyList<KeyStroke> strokes);

    bool SendEnter();
}

/// <summary>
/// Puts a chat line into the sim's open chat box without the focus (spike 16; spec-feedback 37): the text as
/// characters and Enter as a key, posted to the sim's window. Implemented with window messages in the IRacing project.
/// </summary>
public interface IChatPoster
{
    /// <summary>False if Windows refused a message (for example to an iRacing running as administrator).</summary>
    bool PostText(WindowHandle window, string text);

    bool PostEnter(WindowHandle window);
}

/// <summary>The SDK's chat broadcast messages. They do not need focus.</summary>
public interface ISimChat
{
    void BeginChat();

    void CancelChat();
}
