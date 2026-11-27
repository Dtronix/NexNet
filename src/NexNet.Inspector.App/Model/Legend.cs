using NexNet.Inspector.App.Controls.Hex;
using NexNet.Inspector.App.Controls.Timeline;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;

namespace NexNet.Inspector.App.Model;

/// <summary>A section of the legend (Legend &amp; symbols), in reading order.</summary>
public enum LegendSection : byte
{
    /// <summary>The frame flag chips.</summary>
    FrameFlags = 0,

    /// <summary>Direction glyphs, colours and the ladder's arrows.</summary>
    Direction = 1,

    /// <summary>The transport op chips.</summary>
    TransportOps = 2,

    /// <summary>Count badges and the title bar's pills.</summary>
    Badges = 3,

    /// <summary>The timeline.</summary>
    Timeline = 4,

    /// <summary>The ladder.</summary>
    Ladder = 5,

    /// <summary>The hex stream.</summary>
    Hex = 6,

    /// <summary>The inspector's assembly bar.</summary>
    Inspector = 7,

    /// <summary>The Compare workspace.</summary>
    Compare = 8,

    /// <summary>Channels, collections and the duplex pipes that carry them.</summary>
    Pipes = 9,
}

/// <summary>One symbol the legend explains; <see cref="Controls.LegendSwatch"/> draws each with the views' own tokens and draw paths.</summary>
public enum LegendSymbol : byte
{
    FlagTransportSplit,
    FlagNexNetSplit,
    FlagCancel,
    FlagRedacted,
    FlagIncomplete,

    Inbound,
    Outbound,
    LadderArrows,
    Lifelines,

    ReadOp,
    WriteOp,
    OpRange,
    SplitOp,

    CountBadge,
    DifferenceBadge,
    SyncedBadge,
    RecordingPill,
    RecordAllPill,
    ScopeChip,
    Bookmarks,

    Overview,
    RangeHandles,
    Ruler,
    RulerSelection,
    LaneGroups,
    Block,
    SplitMarks,
    Span,
    TracedSpan,
    CancelledSpan,
    PipeLifetime,
    TraceBand,
    Gap,
    Playhead,
    PinnedLane,

    BracketMember,
    BracketInterleaved,
    TimeScaled,

    HexNone,
    HexPreamble,
    HexType,
    HexLength,
    HexPostHeader,
    HexBody,
    HexBodySelected,
    HexOtherFrame,
    HexReadRule,
    HexFrameAnnotation,
    HexRedacted,
    HexSelectedField,
    HexHoveredField,
    HexBookmark,

    AssemblyStart,
    AssemblyChunks,
    AssemblyAbort,
    AssemblyInterleaved,
    AssemblyHandshake,
    OutOfScopeStep,

    FileA,
    FileB,
    Heat,
    PackedSpan,
    Differs,
    DeltaStrip,
    DiffBand,
    PinnedEntity,

    PipeChannel,
    PipeRaw,
    PipeCollection,
    ChipChannel,
    ChipCollection,
}

/// <summary>One legend line: a symbol, its section, its name and one short sentence.</summary>
/// <param name="Symbol">What the swatch draws.</param>
/// <param name="Section">Where it is listed.</param>
/// <param name="Name">What the user calls it.</param>
/// <param name="Text">What it means.</param>
public readonly record struct LegendEntry(LegendSymbol Symbol, LegendSection Section, string Name, string Text);

/// <summary>
/// The legend's content in one place: every symbol the views draw, by section, in reading order. The dialog, the
/// docs and the coverage tests read this table; the frame flags and hex fills map onto it by enum
/// (<see cref="For(FrameFlags)"/>, <see cref="For(HexCellRole)"/>), so a new flag or role without a line fails a test.
/// </summary>
public static class Legend
{
    private static readonly LegendEntry[] Table =
    [
        new(LegendSymbol.FlagTransportSplit, LegendSection.FrameFlags, "T  Transport split", "The frame arrived over (or was written by) more than one socket op."),
        new(LegendSymbol.FlagNexNetSplit, LegendSection.FrameFlags, "N  NexNet split",
            "NexNet split the message across frames: MessageStart / MessageChunk / Abort, pipe writes, channel items crossing frames."),
        new(LegendSymbol.FlagCancel, LegendSection.FrameFlags, "X  Cancelled", "An InvocationCancellation, a Canceled result or an aborted message."),
        new(LegendSymbol.FlagRedacted, LegendSection.FrameFlags, "R  Redacted", "Bytes replaced by the redaction marker before capture (e.g. the auth token)."),
        new(LegendSymbol.FlagIncomplete, LegendSection.FrameFlags, "…  Incomplete", "The frame is still arriving, or the capture cut it off."),

        new(LegendSymbol.Inbound, LegendSection.Direction, "▼ Inbound", "Bytes the tapped endpoint read."),
        new(LegendSymbol.Outbound, LegendSection.Direction, "▲ Outbound", "Bytes the tapped endpoint wrote."),
        new(LegendSymbol.LadderArrows, LegendSection.Direction, "Ladder arrows", "One per frame, pointing the way it travelled, in its direction's colour."),
        new(LegendSymbol.Lifelines, LegendSection.Direction, "Lifelines", "The remote peer on the left, the tapped endpoint on the right."),

        new(LegendSymbol.ReadOp, LegendSection.TransportOps, "#n  Read op", "Socket read n of the session."),
        new(LegendSymbol.WriteOp, LegendSection.TransportOps, "wN  Write op", "Socket write N of the session."),
        new(LegendSymbol.OpRange, LegendSection.TransportOps, "#a–#b  Op range", "The frame spans reads a to b."),
        new(LegendSymbol.SplitOp, LegendSection.TransportOps, "Split op", "Outlined in the T colour: the frame was split across this op."),

        new(LegendSymbol.CountBadge, LegendSection.Badges, "Count badge", "How many rows and marks a group holds in view."),
        new(LegendSymbol.DifferenceBadge, LegendSection.Badges, "Difference count", "Compare: how many matched entities differ."),
        new(LegendSymbol.SyncedBadge, LegendSection.Badges, "synced", "The pane follows the timeline's window, playhead and selection; unsynced: detached."),
        new(LegendSymbol.RecordingPill, LegendSection.Badges, "REC  Recording", "Recording the attached session; time since attach."),
        new(LegendSymbol.RecordAllPill, LegendSection.Badges, "ALL · n  Record all", "Recording every session of the server; n sessions so far."),
        new(LegendSymbol.ScopeChip, LegendSection.Badges, "Scope chip",
            "Every view is limited to this time range (Filter to time range…, Ctrl+Shift+F). ‹n: scopes nested; click or Alt+Left goes back, ✕ clears."),
        new(LegendSymbol.Bookmarks, LegendSection.Badges, "★  Bookmarks",
            "Saved views of this recording, n of them (accent once there is one): Ctrl+Shift+B adds the current view and its scope, Alt+1…9 jump back; the list renames (F2) and deletes."),

        new(LegendSymbol.Overview, LegendSection.Timeline, "Overview", "The whole session's frames; the box is the visible window. Double-click to fit all."),
        new(LegendSymbol.RangeHandles, LegendSection.Timeline, "Range handles",
            "Drag the box's edges to move the window's start or end, its middle to pan; Esc cancels."),
        new(LegendSymbol.Ruler, LegendSection.Timeline, "Ruler", "Time in the window; click to move the playhead."),
        new(LegendSymbol.RulerSelection, LegendSection.Timeline, "Ruler selection",
            "Drag across the ruler (timeline or Compare): release zooms to the range, Esc cancels."),
        new(LegendSymbol.LaneGroups, LegendSection.Timeline, "Lane groups", GroupList()),
        new(LegendSymbol.Block, LegendSection.Timeline, "Block", "A read, write or frame in its direction's colour; red when cancelled."),
        new(LegendSymbol.SplitMarks, LegendSection.Timeline, "Split marks", "Thin T / N lines where a frame was split."),
        new(LegendSymbol.Span, LegendSection.Timeline, "Invocation span", "An invocation, from its call to its result."),
        new(LegendSymbol.TracedSpan, LegendSection.Timeline, "Traced span", "The traced invocation."),
        new(LegendSymbol.CancelledSpan, LegendSection.Timeline, "Cancelled span", "A cancelled invocation: red right edge."),
        new(LegendSymbol.PipeLifetime, LegendSection.Timeline, "Pipe lifetime", "A pipe or channel from open (green) to close (red)."),
        new(LegendSymbol.TraceBand, LegendSection.Timeline, "Trace band", "The traced message's time range, dashed edges."),
        new(LegendSymbol.Gap, LegendSection.Timeline, "Gap", "Events lost: Drop-mode overflow skipped ops here."),
        new(LegendSymbol.Playhead, LegendSection.Timeline, "Playhead", "The current time every view follows."),
        new(LegendSymbol.PinnedLane, LegendSection.Timeline, "◆  Pinned lane", "Stays in view, 24 px tall (P, or the lane menu's Expand)."),

        new(LegendSymbol.BracketMember, LegendSection.Ladder, "Trace bracket", "Solid: the traced message's frames."),
        new(LegendSymbol.BracketInterleaved, LegendSection.Ladder, "Interleaved", "Dotted, row dimmed: another message's frame inside the trace."),
        new(LegendSymbol.TimeScaled, LegendSection.Ladder, "Time-scaled", "The gap above a row grows with the time since the row before."),

        new(LegendSymbol.HexPreamble, LegendSection.Hex, "Preamble", "The connection preamble, or a ping."),
        new(LegendSymbol.HexType, LegendSection.Hex, "Type byte", "The frame's message type."),
        new(LegendSymbol.HexLength, LegendSection.Hex, "Length", "The body length (u16)."),
        new(LegendSymbol.HexPostHeader, LegendSection.Hex, "Post-header", "The pipe id, or a chunk's invocation id."),
        new(LegendSymbol.HexBody, LegendSection.Hex, "Body", "A frame's body."),
        new(LegendSymbol.HexBodySelected, LegendSection.Hex, "Selected body", "The body of the selected frame."),
        new(LegendSymbol.HexOtherFrame, LegendSection.Hex, "Other frame", "A frame other than the selected one."),
        new(LegendSymbol.HexNone, LegendSection.Hex, "No fill", "No frame covers the byte yet (the stream was joined mid-frame)."),
        new(LegendSymbol.HexReadRule, LegendSection.Hex, "read #n → …", "A new transport op starts in the next row."),
        new(LegendSymbol.HexFrameAnnotation, LegendSection.Hex, "▸ Type #seq", "Frames that start in the next row."),
        new(LegendSymbol.HexRedacted, LegendSection.Hex, "Redacted", "Striped ·· cells: bytes the tap redacted."),
        new(LegendSymbol.HexSelectedField, LegendSection.Hex, "Selected field", "The selected field or byte range."),
        new(LegendSymbol.HexHoveredField, LegendSection.Hex, "Hovered field", "The field under the pointer, here or in the tree."),
        new(LegendSymbol.HexBookmark, LegendSection.Hex, "Bookmark", "A bookmarked row."),

        new(LegendSymbol.AssemblyStart, LegendSection.Inspector, "S", "MessageStart: opens a split message, no payload."),
        new(LegendSymbol.AssemblyChunks, LegendSection.Inspector, "1 … n", "MessageChunks, sized by their bytes; the selected frame's is filled."),
        new(LegendSymbol.AssemblyAbort, LegendSection.Inspector, "A", "MessageAbort: the sender gave the message up."),
        new(LegendSymbol.AssemblyInterleaved, LegendSection.Inspector, "Interleaved tick", "Other messages' frames arrived between two fragments."),
        new(LegendSymbol.AssemblyHandshake, LegendSection.Inspector, "Pre / Greet", "The handshake's preamble and greetings, ▼ ▲ by direction."),
        new(LegendSymbol.OutOfScopeStep, LegendSection.Inspector, "⊘  Outside the scope", "Message trace: a step outside the document's scope, dimmed."),

        new(LegendSymbol.FileA, LegendSection.Compare, "File A", "The first compared recording."),
        new(LegendSymbol.FileB, LegendSection.Compare, "File B", "The second compared recording."),
        new(LegendSymbol.Heat, LegendSection.Compare, "Heat", "Frame density of a collapsed group."),
        new(LegendSymbol.PackedSpan, LegendSection.Compare, "Packed span", "An entity's lifetime in an expanded group."),
        new(LegendSymbol.Differs, LegendSection.Compare, "Differs", "The entity differs from its match in the other file."),
        new(LegendSymbol.DeltaStrip, LegendSection.Compare, "Δ strip", "Where matched entities differ, across the session."),
        new(LegendSymbol.DiffBand, LegendSection.Compare, "Diff band", "The selected match's time range in both files."),
        new(LegendSymbol.PinnedEntity, LegendSection.Compare, "Pinned entity", "The selected entity, shown as its own row per file."),

        new(LegendSymbol.PipeChannel, LegendSection.Pipes, "≋  Channel", "A typed channel (Channel<T>): items carried by a duplex pipe, named by their type."),
        new(LegendSymbol.PipeRaw, LegendSection.Pipes, "⇄  Raw pipe", "A duplex pipe carrying bytes without item framing."),
        new(LegendSymbol.PipeCollection, LegendSection.Pipes, "☰  Collection", "A synchronized collection: its messages ride on a duplex pipe."),
        new(LegendSymbol.ChipChannel, LegendSection.Pipes, "CH  Channel frame",
            "Beside the wire type (DuplexPipeWrite …): the frame's pipe carries a channel; the target counts its items (×2, part)."),
        new(LegendSymbol.ChipCollection, LegendSection.Pipes, "CO  Collection frame", "Beside the wire type: the frame's pipe carries a collection."),
    ];

    private static readonly string[] Titles =
        ["Frame flags", "Direction", "Transport ops", "Badges", "Timeline", "Ladder", "Hex stream", "Inspector · assembly bar", "Compare",
            "Channels and pipes"];

    /// <summary>Sections in reading order.</summary>
    public const int SectionCount = (int)LegendSection.Pipes + 1;

    /// <summary>Every line, by section, in reading order.</summary>
    public static IReadOnlyList<LegendEntry> Entries => Table;

    /// <summary>A section's heading.</summary>
    public static string Title(LegendSection section) => Titles[(int)section];

    /// <summary>The line of <paramref name="symbol"/>.</summary>
    public static LegendEntry Get(LegendSymbol symbol)
    {
        foreach (LegendEntry entry in Table)
        {
            if (entry.Symbol == symbol) return entry;
        }

        throw new ArgumentOutOfRangeException(nameof(symbol), symbol, "no legend line");
    }

    /// <summary>The line explaining a frame flag's chip.</summary>
    public static LegendSymbol For(FrameFlags flag) => flag switch
    {
        FrameFlags.TransportSplit => LegendSymbol.FlagTransportSplit,
        FrameFlags.NexNetSplit => LegendSymbol.FlagNexNetSplit,
        FrameFlags.Cancel => LegendSymbol.FlagCancel,
        FrameFlags.Redacted => LegendSymbol.FlagRedacted,
        FrameFlags.Incomplete => LegendSymbol.FlagIncomplete,
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "a legend line explains exactly one flag"),
    };

    /// <summary>The frame flag a flag line's chip shows; null for any other line.</summary>
    public static FrameFlags? FlagOf(LegendSymbol symbol) => symbol switch
    {
        LegendSymbol.FlagTransportSplit => FrameFlags.TransportSplit,
        LegendSymbol.FlagNexNetSplit => FrameFlags.NexNetSplit,
        LegendSymbol.FlagCancel => FrameFlags.Cancel,
        LegendSymbol.FlagRedacted => FrameFlags.Redacted,
        LegendSymbol.FlagIncomplete => FrameFlags.Incomplete,
        _ => null,
    };

    /// <summary>The line explaining the glyph of a pipe that carries <paramref name="content"/>.</summary>
    public static LegendSymbol For(PipeContent content) => content switch
    {
        PipeContent.Channel => LegendSymbol.PipeChannel,
        PipeContent.Collection => LegendSymbol.PipeCollection,
        PipeContent.Raw => LegendSymbol.PipeRaw,
        _ => throw new ArgumentOutOfRangeException(nameof(content), content, "no legend line"),
    };

    /// <summary>The line explaining the CH / CO chip of <paramref name="content"/>; null for a raw pipe, which has none.</summary>
    public static LegendSymbol? ChipFor(PipeContent content) => content switch
    {
        PipeContent.Channel => LegendSymbol.ChipChannel,
        PipeContent.Collection => LegendSymbol.ChipCollection,
        _ => null,
    };

    /// <summary>The pipe kind a glyph line draws; null for any other line.</summary>
    public static PipeContent? PipeOf(LegendSymbol symbol) => symbol switch
    {
        LegendSymbol.PipeChannel => PipeContent.Channel,
        LegendSymbol.PipeRaw => PipeContent.Raw,
        LegendSymbol.PipeCollection => PipeContent.Collection,
        _ => null,
    };

    /// <summary>The pipe kind a chip line draws; null for any other line.</summary>
    public static PipeContent? ChipOf(LegendSymbol symbol) => symbol switch
    {
        LegendSymbol.ChipChannel => PipeContent.Channel,
        LegendSymbol.ChipCollection => PipeContent.Collection,
        _ => null,
    };

    /// <summary>The line explaining a hex cell fill.</summary>
    public static LegendSymbol For(HexCellRole role) => role switch
    {
        HexCellRole.None => LegendSymbol.HexNone,
        HexCellRole.OtherFrame => LegendSymbol.HexOtherFrame,
        HexCellRole.Preamble => LegendSymbol.HexPreamble,
        HexCellRole.Type => LegendSymbol.HexType,
        HexCellRole.Length => LegendSymbol.HexLength,
        HexCellRole.PostHeader => LegendSymbol.HexPostHeader,
        HexCellRole.Body => LegendSymbol.HexBody,
        HexCellRole.BodySelected => LegendSymbol.HexBodySelected,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "no legend line"),
    };

    /// <summary>The hex fill a hex fill line shows; null for any other line.</summary>
    public static HexCellRole? RoleOf(LegendSymbol symbol) => symbol switch
    {
        LegendSymbol.HexNone => HexCellRole.None,
        LegendSymbol.HexOtherFrame => HexCellRole.OtherFrame,
        LegendSymbol.HexPreamble => HexCellRole.Preamble,
        LegendSymbol.HexType => HexCellRole.Type,
        LegendSymbol.HexLength => HexCellRole.Length,
        LegendSymbol.HexPostHeader => HexCellRole.PostHeader,
        LegendSymbol.HexBody => HexCellRole.Body,
        LegendSymbol.HexBodySelected => HexCellRole.BodySelected,
        _ => null,
    };

    // The group names as the timeline's headers spell them, so a renamed group renames its legend line.
    private static string GroupList()
    {
        string[] names = new string[TimelineModel.GroupCount];
        for (int g = 0; g < names.Length; g++) names[g] = TimelineModel.Title((LaneGroupKind)g);
        return string.Join(", ", names) + "; click a header to fold it.";
    }
}
