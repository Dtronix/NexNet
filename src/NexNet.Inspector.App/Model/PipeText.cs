using System.Globalization;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model;

/// <summary>The channel or collection items one frame carried (part of).</summary>
/// <param name="First">Index into <see cref="MessageAssembler.ChannelItems"/> of the first; -1 for none.</param>
/// <param name="Count">How many items the frame carried bytes of.</param>
/// <param name="Partial">True when the frame carried part of one item only: the item crosses frames.</param>
public readonly record struct FrameItems(int First, int Count, bool Partial)
{
    /// <summary>No items (a raw pipe, a control frame, a pipe whose items cannot be split).</summary>
    public static FrameItems None => new(-1, 0, false);
}

/// <summary>
/// How every view names a duplex pipe and what it carries. A typed channel (<c>Channel&lt;T&gt;</c>) or a synchronized
/// collection rides on a duplex pipe, so the wire only knows pipes; the views name the channel or collection first and
/// say which pipe carries it: ≋ for a channel, ☰ for a collection, ⇄ for a raw pipe, and the CH / CO chips beside a
/// frame's wire type.
/// </summary>
public static class PipeText
{
    /// <summary>The channel glyph.</summary>
    public const string ChannelGlyph = "≋";

    /// <summary>The raw pipe glyph.</summary>
    public const string RawGlyph = "⇄";

    /// <summary>The collection glyph.</summary>
    public const string CollectionGlyph = "☰";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The glyph of a pipe that carries <paramref name="content"/>.</summary>
    public static string Glyph(PipeContent content) => content switch
    {
        PipeContent.Channel => ChannelGlyph,
        PipeContent.Collection => CollectionGlyph,
        _ => RawGlyph,
    };

    /// <summary>The chip beside a frame's wire type: "CH" for a channel, "CO" for a collection; null for a raw pipe.</summary>
    public static string? Chip(PipeContent content) => content switch
    {
        PipeContent.Channel => "CH",
        PipeContent.Collection => "CO",
        _ => null,
    };

    /// <summary>The section badge: "CHANNEL", "COLLECTION"; null for a raw pipe.</summary>
    public static string? Badge(PipeContent content) => content switch
    {
        PipeContent.Channel => "CHANNEL",
        PipeContent.Collection => "COLLECTION",
        _ => null,
    };

    /// <summary>What a chip or glyph means, for tooltips and the legend.</summary>
    public static string Describe(PipeContent content) => content switch
    {
        PipeContent.Channel => "Channel: typed items carried by a duplex pipe",
        PipeContent.Collection => "Collection: synchronized collection messages carried by a duplex pipe",
        _ => "Raw duplex pipe: bytes without item framing",
    };

    /// <summary>The short target word of dense rows: "chan", "coll", "pipe".</summary>
    public static string Noun(PipeContent content) => content switch
    {
        PipeContent.Channel => "chan",
        PipeContent.Collection => "coll",
        _ => "pipe",
    };

    /// <summary>"3·1": the client's then the server's local pipe id.</summary>
    public static string Ids(ushort pipeId) =>
        (pipeId & 0xFF).ToString(Invariant) + "·" + (pipeId >> 8).ToString(Invariant);

    /// <summary>
    /// A pipe named by what it carries: "Channel&lt;LogLine&gt; 36·36" (or "Channel 36·36" when the schema does not
    /// say), "Collection Users 1·1", "Pipe 3·1".
    /// </summary>
    /// <param name="content">What the pipe carries.</param>
    /// <param name="pipeId">The pipe id.</param>
    /// <param name="label">The channel's item type or the collection's name (<see cref="SessionDecoder.GetPipeLabel"/>).</param>
    public static string Title(PipeContent content, ushort pipeId, string? label) => content switch
    {
        PipeContent.Channel => label is null ? "Channel " + Ids(pipeId) : "Channel<" + label + "> " + Ids(pipeId),
        PipeContent.Collection => label is null ? "Collection " + Ids(pipeId) : "Collection " + label + " " + Ids(pipeId),
        _ => "Pipe " + Ids(pipeId),
    };

    /// <summary>A session's pipe named by what it carries (see <see cref="Title(PipeContent, ushort, string?)"/>). Call under the session's gate.</summary>
    public static string Title(CaptureSession session, int pipe)
    {
        ArgumentNullException.ThrowIfNull(session);
        PipeRow row = session.Assembler.Pipes[pipe];
        return Title(row.Content, row.PipeId, session.Decoder.GetPipeLabel(pipe));
    }

    /// <summary>The sub-row under a channel or collection: "duplex pipe 36·36".</summary>
    public static string CarriedBy(ushort pipeId) => "duplex pipe " + Ids(pipeId);

    /// <summary>
    /// The item part of a dense row's target: "LogLine ×2" (×n only past one), "LogLine (part)" for an item that crosses
    /// frames, the bare label for one whole item; the count alone without a label; null when there is nothing to say.
    /// </summary>
    public static string? WithItems(string? label, int count, bool partial)
    {
        string? items = count > 1 ? "×" + count.ToString("N0", Invariant) : partial ? "(part)" : null;
        if (items is null) return label;
        return label is null ? items : label + " " + items;
    }

    /// <summary>
    /// The items of a channel or collection pipe that <paramref name="seq"/> carried bytes of: a binary search over the
    /// pipe's items in that direction (their first and last frames both rise). Call under the session's gate.
    /// </summary>
    public static FrameItems Find(MessageAssembler assembler, int pipe, TapDirection direction, long seq)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        if ((uint)pipe >= (uint)assembler.Pipes.Length || assembler.Pipes[pipe].Content == PipeContent.Raw) return FrameItems.None;
        ReadOnlySpan<int> items = assembler.GetPipeItems(pipe, direction);
        ReadOnlySpan<ChannelItemRow> rows = assembler.ChannelItems;
        int lo = 0, hi = items.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (rows[items[mid]].LastSeq < seq) lo = mid + 1;
            else hi = mid;
        }

        int count = 0;
        while (lo + count < items.Length && rows[items[lo + count]].FirstSeq <= seq) count++;
        if (count == 0) return FrameItems.None;
        ref readonly ChannelItemRow first = ref rows[items[lo]];
        bool partial = count == 1 && (first.FragmentCount > 1 || first.State == AssemblyState.Assembling);
        return new FrameItems(items[lo], count, partial);
    }

    /// <summary>An item's 1-based position among its pipe's items of its direction (the inspector's "Item #10").</summary>
    public static int Ordinal(MessageAssembler assembler, in ChannelItemRow item)
    {
        ArgumentNullException.ThrowIfNull(assembler);
        ReadOnlySpan<int> items = assembler.GetPipeItems(item.Pipe, item.Direction);
        int at = items.BinarySearch(item.Index);
        return at < 0 ? 0 : at + 1;
    }
}
