using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Framing;

/// <summary>One frame (or the preamble, or an unframeable range) of one direction of a tapped stream.</summary>
/// <remarks>
/// A record holds positions, not bytes: offsets are absolute within the direction's stream (the sum of the lengths of
/// every op of that direction before it), so the hex view and the recording format can map a record back to the exact
/// op bytes that carried it.
/// </remarks>
public readonly record struct FrameRecord
{
    /// <summary>Position of the record among the records of its direction, from 0.</summary>
    public long Seq { get; init; }

    /// <summary>Which side of the tapped endpoint the bytes travelled.</summary>
    public TapDirection Direction { get; init; }

    /// <summary>What the record covers.</summary>
    public FrameKind Kind { get; init; }

    /// <summary>
    /// The type byte. <see cref="MessageType.Unset"/> for the preamble and for an incomplete frame whose type byte has not
    /// arrived. It may be an unnamed value: an unassigned disconnect code (still a disconnect), or the unknown byte of a
    /// <see cref="FrameKind.Malformed"/> record.
    /// </summary>
    public MessageType Type { get; init; }

    /// <summary>Annotations; see <see cref="FrameFlags"/>.</summary>
    public FrameFlags Flags { get; init; }

    /// <summary>Stopwatch ticks of the op that carried the first byte.</summary>
    public long StartTicks { get; init; }

    /// <summary>Stopwatch ticks of the op that carried the last byte (the latest op so far for an incomplete record).</summary>
    public long EndTicks { get; init; }

    /// <summary>Id of the op that carried the first byte.</summary>
    public long FirstOpId { get; init; }

    /// <summary>Id of the op that carried the last byte (the latest op so far for an incomplete record).</summary>
    public long LastOpId { get; init; }

    /// <summary>Absolute stream offset of the first byte (the type byte, or the first preamble byte).</summary>
    public long Offset { get; init; }

    /// <summary>
    /// Bytes of the record present in the stream. For a complete frame this is
    /// <see cref="HeaderLength"/> + <see cref="PostHeaderLength"/> + <see cref="BodyLength"/>; for an incomplete one it is
    /// what has arrived.
    /// </summary>
    public int Length { get; init; }

    /// <summary>Type byte plus length field: 1 for Ping and disconnects, 3 otherwise, 8 for the preamble.</summary>
    public byte HeaderLength { get; init; }

    /// <summary>Post-header bytes: 2 for a pipe ID, 3 for a MessageChunk's invocation ID and kind, else 0.</summary>
    public byte PostHeaderLength { get; init; }

    /// <summary>The body length the header declares; 0 when there is no body or the length has not arrived.</summary>
    public int BodyLength { get; init; }

    /// <summary>What <see cref="TargetId"/> names.</summary>
    public FrameTarget Target { get; init; }

    /// <summary>
    /// For <see cref="FrameTarget.Pipe"/>, the pipe ID in its body form, <c>clientId | (serverId &lt;&lt; 8)</c>; for
    /// <see cref="FrameTarget.Invocation"/>, the invocation ID. 0 otherwise.
    /// </summary>
    public ushort TargetId { get; init; }

    /// <summary>For a MessageChunk, the message kind of the post-header (0 Argument, 1 Result); 0 otherwise.</summary>
    public byte ChunkKind { get; init; }

    /// <summary>Absolute stream offset of the body.</summary>
    public long BodyOffset => Offset + HeaderLength + PostHeaderLength;

    /// <summary>The client's local pipe ID, for a <see cref="FrameTarget.Pipe"/> target.</summary>
    public byte ClientPipeId => (byte)TargetId;

    /// <summary>The server's local pipe ID, for a <see cref="FrameTarget.Pipe"/> target.</summary>
    public byte ServerPipeId => (byte)(TargetId >> 8);
}
