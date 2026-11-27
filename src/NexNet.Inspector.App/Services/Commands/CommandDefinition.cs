using Avalonia.Input;

namespace NexNet.Inspector.App.Services.Commands;

/// <summary>What the palette and the app menu show for one <see cref="AppCommand"/>.</summary>
/// <param name="Command">The command.</param>
/// <param name="Title">Its title, as a menu item reads ("Go to time…").</param>
/// <param name="Group">The app menu section.</param>
/// <param name="Glyph">The icon column's glyph.</param>
/// <param name="Gesture">Its shortcut; null for none. Gestures with Ctrl or Alt, and function keys, are bound window-wide.</param>
/// <param name="Hint">Secondary text after the title in the palette ("e.g. :1262.4"); null for none.</param>
/// <param name="InPalette">
/// False for a command only its shortcut runs (Alt+1…9 bookmark jumps): the palette does not offer it, so a typed
/// number finds invocations and frames, not "Jump to bookmark 4".
/// </param>
public sealed record CommandDefinition(
    AppCommand Command,
    string Title,
    CommandGroup Group,
    CommandGlyph Glyph,
    KeyGesture? Gesture = null,
    string? Hint = null,
    bool InPalette = true)
{
    /// <summary>The shortcut as the palette and menus print it ("Ctrl+Shift+P"); null for none.</summary>
    public string? ShortcutText { get; } = Format(Gesture);

    // Avalonia prints a digit key by its name ("Alt+D1"); menus and the palette print the digit ("Alt+1").
    private static string? Format(KeyGesture? gesture)
    {
        if (gesture is null) return null;
        string text = gesture.ToString();
        string name = gesture.Key.ToString();
        return gesture.Key is >= Key.D0 and <= Key.D9 && text.EndsWith(name, StringComparison.Ordinal)
            ? string.Concat(text.AsSpan(0, text.Length - name.Length), ((char)('0' + (gesture.Key - Key.D0))).ToString())
            : text;
    }

    /// <summary>
    /// True when the shortcut can be bound on the window: it has Ctrl or Alt, or is a function key. A bare letter (T)
    /// belongs to the view under the pointer, or typing it into a text box would run it.
    /// </summary>
    public bool IsWindowShortcut =>
        Gesture is { } g && g.Key != Key.Tab
        && ((g.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0 || g.Key is >= Key.F1 and <= Key.F24);
}
