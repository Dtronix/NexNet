namespace NexNet.Inspector.Core.Compare;

/// <summary>How the two sessions of a compare are placed on one timeline.</summary>
public enum Alignment : byte
{
    /// <summary>Each session from its own start (t0): both begin at 0.</summary>
    Relative = 0,

    /// <summary>
    /// By each file's wall-clock anchor: the sessions keep the real time between them, so two sides of one run line up
    /// to the network latency (and the clocks' skew when they were recorded on different machines).
    /// </summary>
    WallClock = 1,

    /// <summary>On an event picked in each session (by default "Session ready", the ServerGreeting).</summary>
    OnEvent = 2,
}

/// <summary>One of the two compared documents.</summary>
public enum CompareSide : byte
{
    /// <summary>The first document (the reference).</summary>
    A = 0,

    /// <summary>The second document.</summary>
    B = 1,
}

/// <summary>
/// Which way bytes travel on the wire, independent of which end tapped them: the client side's Outbound and the server
/// side's Inbound are both <see cref="ClientToServer"/>. Compare keys entities by flow, so a client capture and a server
/// capture of the same connection (mirrored directions) match entity for entity.
/// </summary>
public enum WireFlow : byte
{
    /// <summary>From the client to the server.</summary>
    ClientToServer = 0,

    /// <summary>From the server to the client.</summary>
    ServerToClient = 1,
}

/// <summary>The kinds of entity in the union, in the order the Entities sidebar groups them.</summary>
public enum CompareEntityKind : byte
{
    /// <summary>An invocation (a method or collection call): request, result, cancellation.</summary>
    Invocation = 0,

    /// <summary>A duplex pipe or typed channel, collections excepted.</summary>
    Pipe = 1,

    /// <summary>A synchronized collection's pipe.</summary>
    Collection = 2,

    /// <summary>The frames of one message type and flow that belong to no entity (pings, credits, disconnects).</summary>
    Control = 3,

    /// <summary>A preamble or greeting.</summary>
    Handshake = 4,
}

/// <summary>What a <see cref="CompareEntityKind.Handshake"/> entity is; its <see cref="EntityKey.Id"/>.</summary>
public enum HandshakeItem : ushort
{
    /// <summary>The 8-byte connection preamble.</summary>
    Preamble = 0,

    /// <summary>ClientGreeting.</summary>
    ClientGreeting = 1,

    /// <summary>ClientGreetingReconnection.</summary>
    ClientReconnection = 2,

    /// <summary>ServerGreeting.</summary>
    ServerGreeting = 3,
}

/// <summary>Which documents hold an entity.</summary>
public enum EntityPresence : byte
{
    /// <summary>Both.</summary>
    Both = 0,

    /// <summary>Only document A.</summary>
    OnlyA = 1,

    /// <summary>Only document B.</summary>
    OnlyB = 2,
}

/// <summary>How an entity ended, in one document; one scale for every kind so the two sides compare directly.</summary>
public enum EntityOutcome : byte
{
    /// <summary>Absent.</summary>
    None = 0,

    /// <summary>Seen; the kind has no outcome (handshake, control).</summary>
    Seen = 1,

    /// <summary>An invocation without a result yet.</summary>
    Pending = 2,

    /// <summary>An invocation that completed.</summary>
    Completed = 3,

    /// <summary>An invocation that threw.</summary>
    Exception = 4,

    /// <summary>An invocation that was canceled.</summary>
    Canceled = 5,

    /// <summary>An invocation that authorization refused.</summary>
    Unauthorized = 6,

    /// <summary>A one-way (IgnoreReturn) invocation.</summary>
    OneWay = 7,

    /// <summary>A refused invocation stream or pipe.</summary>
    Refused = 8,

    /// <summary>A pipe still open.</summary>
    Open = 9,

    /// <summary>A pipe one side completed.</summary>
    Closing = 10,

    /// <summary>A pipe both sides completed.</summary>
    Closed = 11,
}

/// <summary>What differs between the two sides of an entity.</summary>
[Flags]
public enum EntityDifferences : ushort
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Only one document holds the entity.</summary>
    Presence = 1 << 0,

    /// <summary>The outcomes differ (result state, pipe lifecycle).</summary>
    Outcome = 1 << 1,

    /// <summary>The payload bytes differ (arguments, result, pipe data, greeting body).</summary>
    Content = 1 << 2,

    /// <summary>The numbers of channel items or collection messages differ.</summary>
    Items = 1 << 3,

    /// <summary>The wire bytes of the entity's frames differ.</summary>
    Bytes = 1 << 4,

    /// <summary>The numbers of frames differ.</summary>
    Frames = 1 << 5,

    /// <summary>The aligned start or the duration differs by more than the tolerance.</summary>
    Timing = 1 << 6,
}

/// <summary>How much a difference matters; the Δ strip and "Only differences" filter on it.</summary>
public enum DeltaSeverity : byte
{
    /// <summary>No difference.</summary>
    None = 0,

    /// <summary>Timing only.</summary>
    Info = 1,

    /// <summary>Framing only: frame counts or wire sizes, the same content.</summary>
    Low = 2,

    /// <summary>Content: payload bytes or item counts; or a capture gap, which makes nearby differences suspect.</summary>
    Medium = 3,

    /// <summary>An entity missing from one side, or a different outcome.</summary>
    High = 4,
}

/// <summary>What a <see cref="DeltaEntry"/> describes.</summary>
public enum DeltaKind : byte
{
    /// <summary>A union entity whose sides differ.</summary>
    Entity = 0,

    /// <summary>A gap in one capture (dropped data).</summary>
    Gap = 1,
}

/// <summary>How a field compares, reading from A to B.</summary>
public enum FieldDiffKind : byte
{
    /// <summary>Present in both with the same bytes (the Field diff table lists these untinted).</summary>
    Same = 0,

    /// <summary>Only in B.</summary>
    Added = 1,

    /// <summary>Only in A.</summary>
    Removed = 2,

    /// <summary>In both, with different bytes, kinds or children.</summary>
    Changed = 3,
}

/// <summary>Which decoded value of an entity a <see cref="FieldDiffPart"/> compares.</summary>
public enum FieldDiffPartKind : byte
{
    /// <summary>An invocation's arguments.</summary>
    Arguments = 0,

    /// <summary>An invocation's result value (or exception detail).</summary>
    Result = 1,

    /// <summary>One item of a typed channel.</summary>
    ChannelItem = 2,

    /// <summary>One collection message.</summary>
    CollectionMessage = 3,

    /// <summary>One data run of a raw pipe (one write), compared as bytes.</summary>
    PipeData = 4,

    /// <summary>A greeting's body.</summary>
    Greeting = 5,
}

/// <summary>What an <see cref="AlignmentEvent"/> aligns on.</summary>
public enum AlignmentEventKind : byte
{
    /// <summary>The ServerGreeting frame of each session ("Session ready").</summary>
    SessionReady = 0,

    /// <summary>The start of a union entity (its first frame) in each session, such as a given invocation.</summary>
    Entity = 1,

    /// <summary>A frame picked in each session.</summary>
    Frames = 2,
}
