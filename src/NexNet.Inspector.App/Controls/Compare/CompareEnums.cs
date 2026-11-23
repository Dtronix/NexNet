namespace NexNet.Inspector.App.Controls.Compare;

/// <summary>How a collapsed category of the aligned view draws (the header's Rows: Heat | Packed).</summary>
public enum CompareRowMode : byte
{
    /// <summary>A 16 px heat row: frame density as the opacity of the file's colour.</summary>
    Heat = 0,

    /// <summary>An 18 px packed row: every entity of the category as an 8 px span.</summary>
    Packed = 1,
}

/// <summary>The row categories of the aligned view and the Entities sidebar, in display order.</summary>
public enum CompareCategory : byte
{
    /// <summary>Every frame of the session (the aligned view's first row; no entities).</summary>
    Transport = 0,

    /// <summary>Invocations, grouped by method.</summary>
    Invocations = 1,

    /// <summary>Duplex pipes and typed channels.</summary>
    Pipes = 2,

    /// <summary>Synchronized collections.</summary>
    Collections = 3,

    /// <summary>Frames that belong to no entity: pings, credits, cancellations, disconnects.</summary>
    Control = 4,

    /// <summary>The preamble and the greetings.</summary>
    Handshake = 5,
}

/// <summary>What a row of the aligned view is.</summary>
public enum CompareRowKind : byte
{
    /// <summary>A file's 24 px header: colour square, name, side, recorded, offset.</summary>
    FileHeader = 0,

    /// <summary>A collapsed category: a heat or packed row of all its frames or entities.</summary>
    Collapsed = 1,

    /// <summary>An expanded category's header (its groups follow it).</summary>
    Expanded = 2,

    /// <summary>One group of an expanded category (one method's invocations, one pipe…): a packed row.</summary>
    Group = 3,

    /// <summary>The selected entity, pinned under its category (accent.trace).</summary>
    Pinned = 4,
}

/// <summary>What lies under a point of the aligned view.</summary>
public enum CompareArea : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The ruler.</summary>
    Ruler = 1,

    /// <summary>The Δ differences strip.</summary>
    Deltas = 2,

    /// <summary>A row's gutter (caret, label, count, Δ flag).</summary>
    Gutter = 3,

    /// <summary>A row's track.</summary>
    Track = 4,
}

/// <summary>Which parts of the aligned view a change touches.</summary>
[Flags]
public enum CompareChanges : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>The rows (session, expansion, filter, mode, selection): their layout is rebuilt.</summary>
    Rows = 1 << 0,

    /// <summary>The time window.</summary>
    Window = 1 << 1,

    /// <summary>The playhead.</summary>
    Playhead = 1 << 2,

    /// <summary>The selected entity.</summary>
    Selection = 1 << 3,

    /// <summary>The alignment (offsets, timing differences).</summary>
    Alignment = 1 << 4,
}
