using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.App.Model;

/// <summary>What bytes a right-clicked item maps to.</summary>
public enum ContentBytesKind : byte
{
    /// <summary>None: the item is text only (a diff row, an entity, a session).</summary>
    None = 0,

    /// <summary>A whole frame (header and body) of one direction; its body is the decodable part.</summary>
    Frame = 1,

    /// <summary>A contiguous byte range of one direction (a field, a hex selection).</summary>
    Range = 2,

    /// <summary>A range of a reassembled message's payload that spans frames (a field of a large message).</summary>
    Payload = 3,
}

/// <summary>
/// The bytes behind a content menu's item: a frame (with the message it is part of, so the menu can also offer the
/// reassembled body), a contiguous stream range, or a payload range of a reassembled message.
/// </summary>
/// <param name="Kind">What the item maps to.</param>
/// <param name="Direction">The stream the bytes are in.</param>
/// <param name="Range">The whole contiguous bytes: the frame, or the range; empty for a payload.</param>
/// <param name="Body">The contiguous bytes Open as ▸ reads: a frame's body, or the range; empty for a payload.</param>
/// <param name="Message">The reassembled message a frame is part of; <see cref="MessageRef.None"/> when it stands alone.</param>
/// <param name="Payload">For <see cref="ContentBytesKind.Payload"/>, the range in the message's coordinates.</param>
public readonly record struct ContentBytes(
    ContentBytesKind Kind,
    TapDirection Direction,
    StreamRange Range,
    StreamRange Body,
    MessageRef Message,
    PayloadRange Payload)
{
    /// <summary>No bytes.</summary>
    public static ContentBytes None => new(ContentBytesKind.None, TapDirection.Inbound, default, default, MessageRef.None, PayloadRange.None);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Kind == ContentBytesKind.None;

    /// <summary>A frame's bytes; <paramref name="message"/> is the assembled message or channel item it is part of, if any.</summary>
    public static ContentBytes OfFrame(in FrameRecord frame, MessageRef message) => new(ContentBytesKind.Frame, frame.Direction,
        new StreamRange(frame.Offset, frame.Length), new StreamRange(frame.BodyOffset, Math.Max(0, frame.Offset + frame.Length - frame.BodyOffset)),
        message.Kind is MessageRefKind.Assembled or MessageRefKind.ChannelItem ? message : MessageRef.None, PayloadRange.None);

    /// <summary>A contiguous range of one direction's stream.</summary>
    public static ContentBytes OfRange(TapDirection direction, StreamRange range) =>
        new(ContentBytesKind.Range, direction, range, range, MessageRef.None, PayloadRange.None);

    /// <summary>A range of a message's payload that is not contiguous in the stream.</summary>
    public static ContentBytes OfPayload(in PayloadRange payload) =>
        new(ContentBytesKind.Payload, payload.Message.Direction, default, default, payload.Message, payload);
}

/// <summary>
/// One right-clicked (or Ctrl+C'd) item of a content view, as its context menu offers it: a header (title and detail),
/// the text the row shows (Copy), its value (Copy value), its path in a tree (Copy path), the row as tab-separated
/// cells (Copy row as TSV), and the bytes it maps to. Views make one when the menu opens.
/// </summary>
public sealed class ContentItem
{
    /// <summary>The menu header's bold part: "Frame #1,204", "Text".</summary>
    public required string Title { get; init; }

    /// <summary>The header's secondary part: "Invocation · 42 B"; null for none.</summary>
    public string? Detail { get; init; }

    /// <summary>What Copy (Ctrl+C) puts on the clipboard: the item as displayed.</summary>
    public required string Text { get; init; }

    /// <summary>What Copy value puts on the clipboard; null hides the item.</summary>
    public string? Value { get; init; }

    /// <summary>What Copy path puts on the clipboard (tree fields); null hides the item.</summary>
    public string? Path { get; init; }

    /// <summary>The row's cells, tab-separated (Copy row as TSV); null hides the item.</summary>
    public string? Row { get; init; }

    /// <summary>The bytes the item maps to.</summary>
    public ContentBytes Bytes { get; init; } = ContentBytes.None;

    /// <summary>The stem of a saved file's suggested name: "inbound-frame-1204".</summary>
    public string FileStem { get; init; } = "bytes";

    /// <summary>Cells joined by tabs, with tabs and line breaks inside a cell flattened to spaces.</summary>
    public static string Tsv(params ReadOnlySpan<string?> cells)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) text.Append('\t');
            foreach (var c in cells[i] ?? "")
                text.Append(c is '\t' or '\r' or '\n' ? ' ' : c);
        }

        return text.ToString();
    }
}
