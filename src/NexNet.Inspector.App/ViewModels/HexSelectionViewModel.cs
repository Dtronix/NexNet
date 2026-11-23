using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.ViewModels;

/// <summary>How "Copy as" writes the selected bytes.</summary>
public enum ByteCopyFormat : byte
{
    /// <summary>Space-separated hex pairs.</summary>
    Hex = 0,

    /// <summary>A C# byte array initializer.</summary>
    CSharp = 1,

    /// <summary>Base64.</summary>
    Base64 = 2,
}

/// <summary>
/// The hex selection menu (screen context-menus): what is selected (the header's "16 bytes selected 1A3F21 – 1A3F30")
/// and what the menu does with it: copy as hex, a C# byte array or Base64; decode it another way in the inspector
/// ("Decode selection as ▸"); go to its frame's start or to a typed offset; bookmark its first byte; export it to a file.
/// Made when the menu opens, over the hex pane's model and the sync bus's selection at that moment.
/// </summary>
public sealed class HexSelectionViewModel
{
    /// <summary>The most bytes copied or exported at once; a larger selection disables them.</summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    private readonly HexViewModel _hex;
    private readonly Func<string, Task>? _copy;

    /// <param name="hex">The hex pane's model (its document, sync bus, bookmarks and dialog host).</param>
    /// <param name="copy">Puts text on the clipboard; null disables the Copy items.</param>
    public HexSelectionViewModel(HexViewModel hex, Func<string, Task>? copy)
    {
        ArgumentNullException.ThrowIfNull(hex);
        _hex = hex;
        _copy = copy;
        Selection = hex.Sync.Selection;
    }

    /// <summary>The selection the menu acts on.</summary>
    public Selection Selection { get; }

    /// <summary>True when the selection is a byte range (otherwise every item is disabled).</summary>
    public bool HasBytes => Selection.Kind == SelectionKind.ByteRange && Selection.Bytes.Length > 0;

    /// <summary>The selected stream.</summary>
    public TapDirection Direction => Selection.Direction;

    /// <summary>The first selected byte.</summary>
    public long Offset => Selection.Bytes.Offset;

    /// <summary>The number of bytes selected.</summary>
    public long Length => HasBytes ? Selection.Bytes.Length : 0;

    /// <summary>The header's bold part: "16 bytes selected".</summary>
    public string CountText => HasBytes ? ByteText.Count(Length) : "No bytes selected";

    /// <summary>The header's mono part: "1A3F21 – 1A3F30".</summary>
    public string RangeText => HasBytes ? ByteText.Range(Offset, Length) : "";

    /// <summary>True when the bytes can be read and are few enough to copy or export.</summary>
    public bool CanRead => HasBytes && Length <= MaxBytes && ReadBytes() is not null;

    /// <summary>True when Copy is possible.</summary>
    public bool CanCopy => _copy is not null && CanRead;

    /// <summary>The reading the inspector shows the selection in (the submenu's check).</summary>
    public DecodeAs Decode => _hex.Sync.Decode.Covers(Selection) ? _hex.Sync.Decode.Mode : DecodeAs.Auto;

    /// <summary>True when the selection lies in a frame: "Go to frame start" has somewhere to go.</summary>
    public bool CanGoToFrameStart => HasBytes && Selection.Frame.Seq >= 0;

    /// <summary>True when "Go to offset…" can open its dialog.</summary>
    public bool CanGoToOffset => _hex.ShowDialog is not null && _hex.Document?.Session is not null;

    /// <summary>The bookmark the menu toggles: the selection's first byte.</summary>
    public HexPosition BookmarkPosition => new(Direction, Offset);

    /// <summary>True when the first byte is bookmarked.</summary>
    public bool IsBookmarked => HasBytes && _hex.IsBookmarked(BookmarkPosition);

    /// <summary>The file name the export suggests: "inbound-1A3F21-16B.bin".</summary>
    public string ExportName => (Direction == TapDirection.Inbound ? "inbound-" : "outbound-") + ByteText.Offset(Offset) + "-" + Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "B.bin";

    /// <summary>The selected bytes, copied out of the capture; null when they are not all held any more.</summary>
    public byte[]? ReadBytes()
    {
        if (!HasBytes || Length > MaxBytes || _hex.Document?.Session is not { } session) return null;
        byte[] bytes = new byte[(int)Length];
        lock (session.Gate)
            return session.GetStore(Direction).TryCopyTo(Offset, bytes) ? bytes : null;
    }

    /// <summary>The selection as <paramref name="format"/> text; null when the bytes cannot be read.</summary>
    public string? Format(ByteCopyFormat format)
    {
        if (ReadBytes() is not { } bytes) return null;
        return format switch
        {
            ByteCopyFormat.CSharp => ByteText.CSharp(bytes),
            ByteCopyFormat.Base64 => ByteText.Base64(bytes),
            _ => ByteText.Hex(bytes),
        };
    }

    /// <summary>Copies the selection as <paramref name="format"/>; false when nothing was copied.</summary>
    public async Task<bool> CopyAsync(ByteCopyFormat format)
    {
        if (_copy is not { } copy || Format(format) is not { } text) return false;
        await copy(text).ConfigureAwait(true);
        return true;
    }

    /// <summary>Shows the selection in the inspector read as <paramref name="mode"/>.</summary>
    public void DecodeSelectionAs(DecodeAs mode)
    {
        if (HasBytes) _hex.Sync.SetDecode(mode);
    }

    /// <summary>Selects the frame that holds the selection: the hex view scrolls to its first header byte.</summary>
    public bool GoToFrameStart()
    {
        if (!CanGoToFrameStart || _hex.Document?.Session is not { } session) return false;
        FrameRecord frame;
        lock (session.Gate)
        {
            if (!session.TryGetFrame(Selection.Frame, out frame)) return false;
        }

        _hex.Sync.Select(Model.Selection.OfFrame(Selection.Frame), frame.StartTicks);
        return true;
    }

    /// <summary>Opens "Go to offset…" at the selection's first byte, then goes where it says.</summary>
    public Task<HexPosition?> GoToOffsetAsync() => _hex.PromptGoToAsync(new HexPosition(Direction, HasBytes ? Offset : 0));

    /// <summary>Bookmarks the selection's first byte, or drops its bookmark; returns whether it is bookmarked now.</summary>
    public bool ToggleBookmark() => HasBytes && _hex.ToggleBookmark(BookmarkPosition);

    /// <summary>Writes the selected bytes to <paramref name="target"/>; false when they cannot be read.</summary>
    public async Task<bool> ExportAsync(Stream target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReadBytes() is not { } bytes) return false;
        await target.WriteAsync(bytes).ConfigureAwait(true);
        await target.FlushAsync().ConfigureAwait(true);
        return true;
    }
}
