namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>The timeline's lane groups, in the order they are drawn (PKG timeline-lane, inspect-live).</summary>
public enum LaneGroupKind : byte
{
    /// <summary>TRANSPORT: the raw reads and writes.</summary>
    Transport = 0,

    /// <summary>FRAMES: parsed frames, inbound and outbound.</summary>
    Frames = 1,

    /// <summary>HANDSHAKE: the preamble and greetings of a capture from inception.</summary>
    Handshake = 2,

    /// <summary>INVOCATIONS: packed rows of invocation spans.</summary>
    Invocations = 3,

    /// <summary>PIPES · CHANNELS: one lane per raw pipe or typed channel.</summary>
    Pipes = 4,

    /// <summary>COLLECTIONS: one lane per synchronized collection.</summary>
    Collections = 5,

    /// <summary>SESSION CONTROL: pings, credits, pipe windows, cancellations, disconnects.</summary>
    Control = 6,
}

/// <summary>What a lane shows; with an index it keys the lane (<see cref="LaneKey"/>).</summary>
public enum LaneKind : byte
{
    /// <summary>Transport reads (inbound ops).</summary>
    Reads = 0,

    /// <summary>Transport writes (outbound ops).</summary>
    Writes = 1,

    /// <summary>Inbound frames.</summary>
    InboundFrames = 2,

    /// <summary>Outbound frames.</summary>
    OutboundFrames = 3,

    /// <summary>The handshake's "Session" lane.</summary>
    Session = 4,

    /// <summary>One packed row of invocation spans; the index is the row.</summary>
    InvocationRow = 5,

    /// <summary>
    /// A pipe's lane, whatever it carries (raw pipe, typed channel or collection; its content picks the group); the index
    /// is the assembler's pipe index.
    /// </summary>
    Pipe = 6,

    /// <summary>The session-control lane.</summary>
    Control = 7,
}

/// <summary>The glyph in front of a lane's label (timeline-lane README, Anatomy).</summary>
public enum LaneGlyph : byte
{
    /// <summary>No glyph (invocation rows).</summary>
    None = 0,

    /// <summary>▼ inbound.</summary>
    Inbound = 1,

    /// <summary>▲ outbound.</summary>
    Outbound = 2,

    /// <summary>⇄ raw duplex pipe.</summary>
    Pipe = 3,

    /// <summary>☰ collection.</summary>
    Collection = 4,

    /// <summary>◆ session.</summary>
    Session = 5,

    /// <summary>· control.</summary>
    Control = 6,

    /// <summary>≋ a channel riding on a duplex pipe.</summary>
    Channel = 7,
}

/// <summary>How a <see cref="TimelineMark"/> is drawn.</summary>
public enum MarkKind : byte
{
    /// <summary>An 8 px frame or transport block in its direction's colour (16 px with its byte count when expanded).</summary>
    Block = 0,

    /// <summary>A 10 px invocation span with its label.</summary>
    Span = 1,

    /// <summary>A pipe's 2 px lifetime line.</summary>
    Lifetime = 2,

    /// <summary>A pipe's open mark (status.ok).</summary>
    Open = 3,

    /// <summary>A pipe's close mark (status.recording).</summary>
    Close = 4,

    /// <summary>A 2 px full-height transport split mark (split.transport).</summary>
    TransportSplit = 5,

    /// <summary>A 2 px full-height NexNet split mark (split.nexnet).</summary>
    NexNetSplit = 6,
}

/// <summary>Modifiers of a <see cref="TimelineMark"/>.</summary>
[Flags]
public enum MarkFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>A cancelled invocation (3 px status.error right edge) or a cancellation frame (status.error block).</summary>
    Cancelled = 1 << 0,

    /// <summary>A block for a transport op rather than a frame: tracing lights it by time, not by entity.</summary>
    Op = 1 << 1,

    /// <summary>An invocation still waiting for its result: its end is the capture's latest tick.</summary>
    Pending = 1 << 2,
}
