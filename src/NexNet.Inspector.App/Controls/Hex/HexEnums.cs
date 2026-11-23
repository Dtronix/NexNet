namespace NexNet.Inspector.App.Controls.Hex;

/// <summary>Which directions the hex view shows: the pane header's ▼ IN | ▲ OUT toggles.</summary>
[Flags]
public enum HexDirections : byte
{
    /// <summary>Neither (never shown: turning the last one off turns the other on).</summary>
    None = 0,

    /// <summary>The bytes the tapped endpoint read.</summary>
    Inbound = 1,

    /// <summary>The bytes it wrote.</summary>
    Outbound = 2,

    /// <summary>Both, interleaved in op order.</summary>
    Both = Inbound | Outbound,
}

/// <summary>What one line of the hex view shows.</summary>
public enum HexLineKind : byte
{
    /// <summary>Up to 16 bytes: offset, hex cells, ASCII.</summary>
    Bytes = 0,

    /// <summary>A read boundary: the transport rule and its "read #A → #B" annotation, above the row a new op starts in.</summary>
    ReadRule = 1,

    /// <summary>The frames that start in the next row: "▸ Type #seq @offset".</summary>
    Frames = 2,
}

/// <summary>The byte role a hex cell is filled by (PKG hex-view "Cell fills by field").</summary>
public enum HexCellRole : byte
{
    /// <summary>No frame covers the byte yet (an incomplete frame, a stream joined mid-frame): no fill.</summary>
    None = 0,

    /// <summary>A frame other than the selected one (color.hex.other-frame, tertiary text).</summary>
    OtherFrame = 1,

    /// <summary>The preamble, or a ping (color.hex.preamble).</summary>
    Preamble = 2,

    /// <summary>The type byte (color.hex.type).</summary>
    Type = 3,

    /// <summary>The u16 body length (color.hex.length).</summary>
    Length = 4,

    /// <summary>The pipe id, or a chunk's invocation id and kind (color.hex.post-header).</summary>
    PostHeader = 5,

    /// <summary>A body, with nothing selected (color.hex.body).</summary>
    Body = 6,

    /// <summary>The body of the selected frame (color.hex.body-selected).</summary>
    BodySelected = 7,
}

/// <summary>Per-cell marks drawn over the fill.</summary>
[Flags]
public enum HexCellMarks : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The tap redacted the byte (striped, "··").</summary>
    Redacted = 1 << 0,

    /// <summary>The first byte of a transport op after another: the 2 px split.transport left border.</summary>
    ReadStart = 1 << 1,

    /// <summary>Part of the selected field or range: 1.5 px accent.trace ring.</summary>
    Selected = 1 << 2,

    /// <summary>Part of the hovered field: 1.5 px direction.inbound ring.</summary>
    Hovered = 1 << 3,

    /// <summary>The byte is no longer held (a live capture trimmed it, or a recording failed to read): drawn blank.</summary>
    Unavailable = 1 << 4,

    /// <summary>The A ⇄ B hex view only: the other document's byte at this position differs (or is missing).</summary>
    Differs = 1 << 5,
}
