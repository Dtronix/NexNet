namespace NexNet.Inspector.Core.Assembly;

/// <summary>What a <see cref="MessageRef"/> points at.</summary>
public enum MessageRefKind : byte
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>A message carried whole by one frame; its body is the frame body.</summary>
    Frame = 1,

    /// <summary>An <see cref="AssembledMessage"/>: a large message or a pipe data run.</summary>
    Assembled = 2,

    /// <summary>A <see cref="ChannelItemRow"/>: one item of a typed channel or collection pipe.</summary>
    ChannelItem = 3,
}

/// <summary>Kinds of <see cref="AssembledMessage"/>.</summary>
public enum AssembledMessageKind : byte
{
    /// <summary>MessageStart, MessageChunks and any MessageAbort of one large argument or result (spec §6).</summary>
    Large = 0,

    /// <summary>
    /// DuplexPipeWrite frames of one pipe and direction that carry one write split by the writer: every frame but the
    /// last is a full 65,535-byte chunk.
    /// </summary>
    PipeRun = 1,
}

/// <summary>How far an assembled message or channel item got.</summary>
public enum AssemblyState : byte
{
    /// <summary>More fragments are expected.</summary>
    Assembling = 0,

    /// <summary>Every byte arrived.</summary>
    Complete = 1,

    /// <summary>A MessageAbort ended a large message.</summary>
    Aborted = 2,

    /// <summary>The capture, the pipe or the session ended before the last byte.</summary>
    Truncated = 3,

    /// <summary>Chunks of a large message whose MessageStart was not captured (attached mid-stream).</summary>
    Orphan = 4,

    /// <summary>A channel item whose length prefix is invalid; the pipe stops being split.</summary>
    Malformed = 5,
}

/// <summary>Which way a large message flows (spec §4.8): the wire values of its kind byte.</summary>
public enum LargeMessageKind : byte
{
    /// <summary>An Invocation's body, from caller to callee.</summary>
    Argument = 0,

    /// <summary>An InvocationResult's body, from callee to caller.</summary>
    Result = 1,
}

/// <summary>Reasons of a MessageAbort (spec §4.9): the wire values.</summary>
public enum MessageAbortReason : byte
{
    /// <summary>The receiver has no budget for the stream.</summary>
    Busy = 0,

    /// <summary>The value is larger than the receiver accepts.</summary>
    TooLarge = 1,

    /// <summary>The sender canceled the stream.</summary>
    Cancelled = 2,

    /// <summary>The sender acknowledges the receiver's abort.</summary>
    Ack = 3,

    /// <summary>Not aborted.</summary>
    None = 255,
}

/// <summary>Where an invocation stands.</summary>
public enum InvocationState : byte
{
    /// <summary>Sent (or being streamed) and no result yet.</summary>
    Pending = 0,

    /// <summary>A CompletedResult arrived.</summary>
    Completed = 1,

    /// <summary>An Exception result arrived.</summary>
    Exception = 2,

    /// <summary>A Canceled result arrived, or the caller aborted its streaming argument.</summary>
    Canceled = 3,

    /// <summary>An Unauthorized result arrived.</summary>
    Unauthorized = 4,

    /// <summary>An IgnoreReturn invocation was sent whole; no result ever follows.</summary>
    OneWay = 5,

    /// <summary>The receiver refused its streamed argument or result, or refused the pipe it opened.</summary>
    Refused = 6,
}

/// <summary>The state byte of an InvocationResult (spec §4.4): the wire values.</summary>
public enum InvocationResultState : byte
{
    /// <summary>Unset.</summary>
    Unset = 0,

    /// <summary>The method completed.</summary>
    CompletedResult = 1,

    /// <summary>The method threw.</summary>
    Exception = 2,

    /// <summary>Authorization refused the call.</summary>
    Unauthorized = 3,

    /// <summary>The method was canceled.</summary>
    Canceled = 4,
}

/// <summary>The state flags of DuplexPipeUpdateState (spec §4.6): the wire values.</summary>
[Flags]
public enum PipeStateFlags : byte
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>The client-to-server direction is complete.</summary>
    ClientWriterServerReaderComplete = 1 << 0,

    /// <summary>The server-to-client direction is complete.</summary>
    ClientReaderServerWriterComplete = 1 << 1,

    /// <summary>The pipe is open.</summary>
    Ready = 1 << 2,

    /// <summary>The open was refused.</summary>
    Refused = 1 << 3,

    /// <summary>Both directions complete; the close message.</summary>
    Complete = ClientWriterServerReaderComplete | ClientReaderServerWriterComplete | Ready,
}

/// <summary>Where a pipe is in its life.</summary>
public enum PipeLifecycle : byte
{
    /// <summary>Ready was seen, or data for a pipe opened before the capture started.</summary>
    Open = 0,

    /// <summary>The registering side refused the open.</summary>
    Refused = 1,

    /// <summary>At least one side sent a completion.</summary>
    Closing = 2,

    /// <summary>Both sides sent Complete; the ID may be reused by a new pipe.</summary>
    Closed = 3,
}

/// <summary>What a pipe's bytes are, which decides whether they are split into channel items.</summary>
public enum PipeContent : byte
{
    /// <summary>An <c>INexusDuplexPipe</c>: unframed bytes.</summary>
    Raw = 0,

    /// <summary>A typed channel: length-prefixed items (spec §7.4).</summary>
    Channel = 1,

    /// <summary>A collection's pipe: channel items that are collection messages (spec §4.11).</summary>
    Collection = 2,
}

/// <summary>Kinds of <see cref="PipeEventRow"/>.</summary>
public enum PipeEventKind : byte
{
    /// <summary>DuplexPipeUpdateState with Ready; carries the initial window.</summary>
    Ready = 0,

    /// <summary>DuplexPipeUpdateState with Refused.</summary>
    Refused = 1,

    /// <summary>DuplexPipeUpdateState with completion flags.</summary>
    State = 2,

    /// <summary>PipeWindowUpdate; carries the credit granted.</summary>
    WindowUpdate = 3,
}

/// <summary>Kinds of greeting.</summary>
public enum GreetingKind : byte
{
    /// <summary>ClientGreeting.</summary>
    Client = 0,

    /// <summary>ClientGreetingReconnection.</summary>
    ClientReconnection = 1,

    /// <summary>ServerGreeting.</summary>
    Server = 2,
}

/// <summary>Collection message tags (spec §4.11): the wire values.</summary>
public enum CollectionMessageKind : ushort
{
    /// <summary>ResetStart.</summary>
    ResetStart = 0,

    /// <summary>ResetValues.</summary>
    ResetValues = 1,

    /// <summary>ResetComplete.</summary>
    ResetComplete = 2,

    /// <summary>Clear.</summary>
    Clear = 3,

    /// <summary>Insert.</summary>
    Insert = 4,

    /// <summary>Replace.</summary>
    Replace = 5,

    /// <summary>Move.</summary>
    Move = 6,

    /// <summary>Remove.</summary>
    Remove = 7,

    /// <summary>Noop.</summary>
    Noop = 8,

    /// <summary>A tag this reader does not know.</summary>
    Unknown = ushort.MaxValue,
}

/// <summary>Which entity list a <see cref="FrameLink"/> points into.</summary>
public enum EntityKind : byte
{
    /// <summary>No entity (preamble, ping, disconnect, credit, or a frame that could not be read).</summary>
    None = 0,

    /// <summary><see cref="MessageAssembler.Greetings"/>.</summary>
    Greeting = 1,

    /// <summary><see cref="MessageAssembler.Invocations"/>.</summary>
    Invocation = 2,

    /// <summary><see cref="MessageAssembler.Pipes"/>.</summary>
    Pipe = 3,
}

/// <summary>Problems assembly found with a frame.</summary>
[Flags]
public enum FrameLinkFlags : byte
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>The body does not decode as its type's envelope.</summary>
    BodyMalformed = 1 << 0,

    /// <summary>The body bytes were no longer (or not yet) available from the stream.</summary>
    BytesUnavailable = 1 << 1,

    /// <summary>The frame belongs to something whose start was not captured.</summary>
    Orphan = 1 << 2,
}
