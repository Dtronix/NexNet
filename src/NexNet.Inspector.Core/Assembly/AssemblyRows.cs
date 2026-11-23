using NexNet.Collections;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Assembly;

/// <summary>Names one logical message: a single frame, an assembled message or a channel item.</summary>
/// <param name="Kind">What <paramref name="Index"/> and <paramref name="Seq"/> name.</param>
/// <param name="Direction">The direction of the message's frames.</param>
/// <param name="Seq">The frame for <see cref="MessageRefKind.Frame"/>; otherwise the first frame of the message.</param>
/// <param name="Index">
/// Index into <see cref="MessageAssembler.Messages"/> or <see cref="MessageAssembler.ChannelItems"/>; -1 for a frame.
/// </param>
public readonly record struct MessageRef(MessageRefKind Kind, TapDirection Direction, long Seq, int Index)
{
    /// <summary>No message.</summary>
    public static MessageRef None => new(MessageRefKind.None, TapDirection.Inbound, -1, -1);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Kind == MessageRefKind.None;

    /// <summary>A message carried by one frame.</summary>
    public static MessageRef ForFrame(TapDirection direction, long seq) => new(MessageRefKind.Frame, direction, seq, -1);
}

/// <summary>
/// A byte range of a message's body, in the message's own coordinates: for a frame, its body; for a large message, the
/// inner message body (the chunk payloads end to end); for a pipe run, its data; for a channel item, its prefix and
/// value. Read the bytes with <see cref="MessageAssembler.TryGetPayload"/>.
/// </summary>
/// <param name="Message">The message.</param>
/// <param name="Offset">Offset of the range in the message.</param>
/// <param name="Length">Length of the range; -1 for <see cref="None"/>.</param>
public readonly record struct PayloadRange(MessageRef Message, long Offset, long Length)
{
    /// <summary>No range (an absent value, or a nil token).</summary>
    public static PayloadRange None => new(MessageRef.None, 0, -1);

    /// <summary>True for <see cref="None"/>.</summary>
    public bool IsNone => Length < 0 || Message.IsNone;
}

/// <summary>What assembly made of one frame: the entity it belongs to and the assembled message it is part of.</summary>
public readonly record struct FrameLink
{
    /// <summary>A frame not linked to anything.</summary>
    public static FrameLink None => new() { EntityIndex = -1, Message = -1 };

    /// <summary>The list <see cref="EntityIndex"/> points into.</summary>
    public EntityKind Entity { get; init; }

    /// <summary>Index of the greeting, invocation or pipe; -1 with <see cref="EntityKind.None"/>.</summary>
    public int EntityIndex { get; init; }

    /// <summary>Index into <see cref="MessageAssembler.Messages"/> when the frame is part of one; otherwise -1.</summary>
    public int Message { get; init; }

    /// <summary>Problems found.</summary>
    public FrameLinkFlags Flags { get; init; }
}

/// <summary>One fragment of a message in a trace: which frame carried which bytes of it.</summary>
/// <param name="Direction">The frame's direction.</param>
/// <param name="Seq">The frame's sequence number.</param>
/// <param name="MessageOffset">Offset of the fragment's first byte in the message.</param>
/// <param name="StreamOffset">Absolute stream offset of the same byte.</param>
/// <param name="Length">Bytes of the message the frame carried; 0 for MessageStart and MessageAbort.</param>
/// <param name="FirstOpId">Transport op that carried the frame's first byte.</param>
/// <param name="LastOpId">Transport op that carried the frame's last byte.</param>
public readonly record struct TraceFragment(
    TapDirection Direction,
    long Seq,
    long MessageOffset,
    long StreamOffset,
    int Length,
    long FirstOpId,
    long LastOpId);

/// <summary>A message assembled from several frames: a large message or a pipe data run.</summary>
public readonly record struct AssembledMessage
{
    /// <summary>Position in <see cref="MessageAssembler.Messages"/>.</summary>
    public int Index { get; init; }

    /// <summary>Large message or pipe run.</summary>
    public AssembledMessageKind Kind { get; init; }

    /// <summary>Direction of the frames that carry the message's bytes.</summary>
    public TapDirection Direction { get; init; }

    /// <summary>How far assembly got.</summary>
    public AssemblyState State { get; init; }

    /// <summary>For a large message, argument or result.</summary>
    public LargeMessageKind LargeKind { get; init; }

    /// <summary>For a large message, the invocation ID that keys the stream.</summary>
    public ushort InvocationId { get; init; }

    /// <summary>For a large message, the inner message type (Invocation or InvocationResult); Unset when unknown.</summary>
    public MessageType InnerType { get; init; }

    /// <summary>For a large message, the announced inner body length; -1 when unknown (orphan) or for a pipe run.</summary>
    public long TotalLength { get; init; }

    /// <summary>Bytes assembled so far.</summary>
    public long Length { get; init; }

    /// <summary>Sequence number of the first frame.</summary>
    public long FirstSeq { get; init; }

    /// <summary>Sequence number of the latest frame of the message's direction.</summary>
    public long LastSeq { get; init; }

    /// <summary>Frames that carried the message, counting MessageStart and MessageAbort.</summary>
    public int FragmentCount { get; init; }

    /// <summary>Invocation of a large message; -1 when not known.</summary>
    public int Invocation { get; init; }

    /// <summary>Pipe of a pipe run; -1 otherwise.</summary>
    public int Pipe { get; init; }

    /// <summary>For a pipe run, offset of its first byte in the pipe's byte stream of that direction.</summary>
    public long PipeOffset { get; init; }

    /// <summary>The first abort seen for a large message; <see cref="MessageAbortReason.None"/> otherwise.</summary>
    public MessageAbortReason AbortReason { get; init; }

    internal int FirstFragment { get; init; }

    internal int LastFragment { get; init; }

    /// <summary>A reference to this message.</summary>
    public MessageRef Ref => new(MessageRefKind.Assembled, Direction, FirstSeq, Index);
}

/// <summary>One invocation: its request, its result and any cancellation.</summary>
public readonly record struct InvocationRow
{
    /// <summary>Position in <see cref="MessageAssembler.Invocations"/>.</summary>
    public int Index { get; init; }

    /// <summary>The invocation ID; meaningless (usually 0) for an IgnoreReturn invocation sent in one frame.</summary>
    public ushort InvocationId { get; init; }

    /// <summary>The method (or collection) ID; valid when <see cref="RequestParsed"/>.</summary>
    public ushort MethodId { get; init; }

    /// <summary>The invocation flags; valid when <see cref="RequestParsed"/>.</summary>
    public InvocationFlags Flags { get; init; }

    /// <summary>True once the request envelope (ID, method, flags) has been read.</summary>
    public bool RequestParsed { get; init; }

    /// <summary>Direction the request travelled: Outbound when the tapped endpoint is the caller.</summary>
    public TapDirection Direction { get; init; }

    /// <summary>Where the invocation stands.</summary>
    public InvocationState State { get; init; }

    /// <summary>The result's state byte; Unset until a result arrived.</summary>
    public InvocationResultState ResultState { get; init; }

    /// <summary>The request message; <see cref="MessageRef.None"/> when it was sent before the capture started.</summary>
    public MessageRef Request { get; init; }

    /// <summary>The result message, once it started; otherwise <see cref="MessageRef.None"/>.</summary>
    public MessageRef Result { get; init; }

    /// <summary>Sequence number of the caller's InvocationCancellation; -1 when none.</summary>
    public long CancellationSeq { get; init; }

    /// <summary>The embedded arguments array in the request, for the schema decoder.</summary>
    public PayloadRange Arguments { get; init; }

    /// <summary>The embedded result value (or exception detail) in the result; None when the result has none.</summary>
    public PayloadRange Value { get; init; }

    /// <summary>The first pipe this invocation opened; -1 when none is known.</summary>
    public int Pipe { get; init; }

    /// <summary>True when the request frame was captured.</summary>
    public bool HasRequest => !Request.IsNone;
}

/// <summary>One duplex pipe, from Ready (or its first captured frame) to both Completes.</summary>
public readonly record struct PipeRow
{
    /// <summary>Position in <see cref="MessageAssembler.Pipes"/>.</summary>
    public int Index { get; init; }

    /// <summary>The pipe ID, <c>clientId | (serverId &lt;&lt; 8)</c>; the opener's partial ID for a refused pipe.</summary>
    public ushort PipeId { get; init; }

    /// <summary>Direction of the opening invocation; null when the open was not captured.</summary>
    public TapDirection? OpenerDirection { get; init; }

    /// <summary>The invocation that opened the pipe; -1 when not known.</summary>
    public int OpenerInvocation { get; init; }

    /// <summary>Where the pipe is in its life.</summary>
    public PipeLifecycle Lifecycle { get; init; }

    /// <summary>What its bytes are; see <see cref="MessageAssembler.SetPipeContent"/>.</summary>
    public PipeContent Content { get; init; }

    /// <summary>Sequence number of the Ready or Refused frame; -1 when the open was not captured.</summary>
    public long OpenSeq { get; init; }

    /// <summary>Last state flags the tapped endpoint received for this pipe.</summary>
    public PipeStateFlags InboundState { get; init; }

    /// <summary>Last state flags the tapped endpoint sent for this pipe.</summary>
    public PipeStateFlags OutboundState { get; init; }

    /// <summary>Data bytes the tapped endpoint received on the pipe.</summary>
    public long InboundBytes { get; init; }

    /// <summary>Data bytes the tapped endpoint sent on the pipe.</summary>
    public long OutboundBytes { get; init; }

    /// <summary>Credit granted to the tapped endpoint by its peer (Ready window plus window updates received).</summary>
    public long InboundGrants { get; init; }

    /// <summary>Credit the tapped endpoint granted its peer (Ready window plus window updates sent).</summary>
    public long OutboundGrants { get; init; }

    /// <summary>Data runs received.</summary>
    public int InboundRuns { get; init; }

    /// <summary>Data runs sent.</summary>
    public int OutboundRuns { get; init; }

    /// <summary>For a collection pipe, its row in <see cref="MessageAssembler.Collections"/>; otherwise -1.</summary>
    public int Collection { get; init; }

    /// <summary>
    /// True when channel items cannot be split: the pipe opened before the capture (no known item boundary), its bytes
    /// were trimmed before splitting, or a length prefix was invalid.
    /// </summary>
    public bool ChannelUnavailable { get; init; }
}

/// <summary>A state change or credit grant of a pipe.</summary>
/// <param name="Pipe">Index into <see cref="MessageAssembler.Pipes"/>.</param>
/// <param name="Direction">Direction of the frame.</param>
/// <param name="Seq">The frame.</param>
/// <param name="Kind">What happened.</param>
/// <param name="State">The state flags (None for a window update).</param>
/// <param name="Window">Credit granted, in bytes.</param>
public readonly record struct PipeEventRow(
    int Pipe,
    TapDirection Direction,
    long Seq,
    PipeEventKind Kind,
    PipeStateFlags State,
    uint Window);

/// <summary>One item of a typed channel or collection pipe: a LEB128 length prefix and one embedded value.</summary>
public readonly record struct ChannelItemRow
{
    /// <summary>Position in <see cref="MessageAssembler.ChannelItems"/>.</summary>
    public int Index { get; init; }

    /// <summary>Index into <see cref="MessageAssembler.Pipes"/>.</summary>
    public int Pipe { get; init; }

    /// <summary>Direction the item travelled.</summary>
    public TapDirection Direction { get; init; }

    /// <summary>How far assembly got.</summary>
    public AssemblyState State { get; init; }

    /// <summary>Offset of the item's first prefix byte in the pipe's byte stream of that direction.</summary>
    public long PipeOffset { get; init; }

    /// <summary>Bytes of the length prefix; 0 while it is incomplete.</summary>
    public int PrefixLength { get; init; }

    /// <summary>Length of the value; -1 while the prefix is incomplete.</summary>
    public int ValueLength { get; init; }

    /// <summary>Bytes of the item (prefix and value) seen so far.</summary>
    public long Length { get; init; }

    /// <summary>First frame that carried part of the item.</summary>
    public long FirstSeq { get; init; }

    /// <summary>Latest frame that carried part of the item.</summary>
    public long LastSeq { get; init; }

    /// <summary>Frames that carried part of the item.</summary>
    public int FragmentCount { get; init; }

    /// <summary>For a collection pipe, the decoded message in <see cref="MessageAssembler.CollectionMessages"/>; -1 otherwise.</summary>
    public int CollectionMessage { get; init; }

    internal int FirstFragment { get; init; }

    internal int LastFragment { get; init; }

    /// <summary>A reference to this item.</summary>
    public MessageRef Ref => new(MessageRefKind.ChannelItem, Direction, FirstSeq, Index);

    /// <summary>The item's value, for the schema decoder; None while the prefix is incomplete.</summary>
    public PayloadRange Value => ValueLength < 0 ? PayloadRange.None : new PayloadRange(Ref, PrefixLength, ValueLength);
}

/// <summary>One synchronized collection: a pipe opened by a collection method.</summary>
public readonly record struct CollectionRow
{
    /// <summary>Position in <see cref="MessageAssembler.Collections"/>.</summary>
    public int Index { get; init; }

    /// <summary>The collection's ID (the method ID of its connect invocation).</summary>
    public ushort CollectionId { get; init; }

    /// <summary>Index into <see cref="MessageAssembler.Pipes"/>.</summary>
    public int Pipe { get; init; }

    /// <summary>Messages decoded.</summary>
    public int MessageCount { get; init; }

    /// <summary>The latest version a message carried; -1 when none did.</summary>
    public int LastVersion { get; init; }
}

/// <summary>One collection message (spec §4.11), decoded from a channel item. Values stay raw for the schema decoder.</summary>
public readonly record struct CollectionMessageRow
{
    /// <summary>Position in <see cref="MessageAssembler.CollectionMessages"/>.</summary>
    public int Index { get; init; }

    /// <summary>Index into <see cref="MessageAssembler.Collections"/>.</summary>
    public int Collection { get; init; }

    /// <summary>Index into <see cref="MessageAssembler.ChannelItems"/>.</summary>
    public int Item { get; init; }

    /// <summary>Direction the message travelled.</summary>
    public TapDirection Direction { get; init; }

    /// <summary>The union tag.</summary>
    public CollectionMessageKind Kind { get; init; }

    /// <summary>The flags element.</summary>
    public NexusCollectionMessageFlags Flags { get; init; }

    /// <summary>The version element; -1 for kinds without one.</summary>
    public int Version { get; init; }

    /// <summary>ResetStart's total values; -1 otherwise.</summary>
    public int TotalValues { get; init; }

    /// <summary>The index (Insert, Replace, Remove) or from-index (Move); -1 otherwise.</summary>
    public int Position { get; init; }

    /// <summary>Move's to-index; -1 otherwise.</summary>
    public int ToPosition { get; init; }

    /// <summary>The embedded value (Insert, Replace) or values (ResetValues) in item coordinates; None otherwise.</summary>
    public PayloadRange Value { get; init; }

    /// <summary>True when the item did not decode as a collection message.</summary>
    public bool Malformed { get; init; }
}

/// <summary>A greeting (spec §4.1, §4.2): single-frame handshake messages, kept for the Handshake view.</summary>
public readonly record struct GreetingRow
{
    /// <summary>Position in <see cref="MessageAssembler.Greetings"/>.</summary>
    public int Index { get; init; }

    /// <summary>Which greeting.</summary>
    public GreetingKind Kind { get; init; }

    /// <summary>Direction of the frame.</summary>
    public TapDirection Direction { get; init; }

    /// <summary>The frame.</summary>
    public long Seq { get; init; }

    /// <summary>Client greetings: the targeted version string, or null.</summary>
    public string? Version { get; init; }

    /// <summary>Client greetings: the server nexus hash.</summary>
    public int ServerNexusHash { get; init; }

    /// <summary>Client greetings: the client nexus hash. Server greeting: the client hash it expects (its version).</summary>
    public int ClientNexusHash { get; init; }

    /// <summary>
    /// Client greetings: the authentication token's bytes within the frame body (None for nil, empty for no token).
    /// Redaction replaces exactly this range.
    /// </summary>
    public PayloadRange Token { get; init; }

    /// <summary>The largest argument, result or channel item the sender accepts.</summary>
    public int MaxMessageSize { get; init; }

    /// <summary>Invocations the sender lets its peer queue.</summary>
    public int InvocationCredits { get; init; }

    /// <summary>Server greeting: the session ID assigned to the client.</summary>
    public long ClientId { get; init; }

    /// <summary>Server greeting: the idle timeout in milliseconds; 0 when not advertised.</summary>
    public uint IdleTimeoutMs { get; init; }
}

/// <summary>Decides what a newly opened pipe carries, from the invocation that opened it.</summary>
/// <remarks>
/// Only the schema knows whether a method's pipe parameter is a raw pipe, a typed channel or a collection, and which
/// argument holds the pipe ID, so the schema layer implements this (<c>Schema.SessionDecoder</c>); without one every
/// pipe stays <see cref="PipeContent.Raw"/> and openers are matched heuristically.
/// </remarks>
public interface IPipeContentResolver
{
    /// <summary>Called once per pipe when its opener is matched.</summary>
    /// <param name="opener">The opening invocation (its method ID is the collection ID for a collection).</param>
    /// <returns>What the pipe carries.</returns>
    PipeContent Resolve(in InvocationRow opener);

    /// <summary>
    /// Says which top-level argument of a DuplexPipe invocation carries the opener's pipe ID, so the pipe is linked to
    /// exactly the invocation that sent it. Called once per such invocation, when its arguments are complete.
    /// </summary>
    /// <param name="invocation">The invocation (its method ID is the collection ID for a collection).</param>
    /// <param name="argument">The 0-based argument index; -1 when the method has no pipe argument.</param>
    /// <returns>
    /// False when the method is not known: the assembler then treats every top-level integer argument 1-255 as a
    /// candidate pipe ID.
    /// </returns>
    bool TryGetPipeArgument(in InvocationRow invocation, out int argument)
    {
        argument = -1;
        return false;
    }
}

internal struct Fragment
{
    public TapDirection Direction;
    public long Seq;
    public long MessageOffset;
    public long StreamOffset;
    public int Length;
    public int Next;
}
