using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// What a large message carries (docs/internals/flow-control.md, section 4.3). With the invocation ID it forms the
/// stream's key; an argument flows from caller to callee, a result from callee to caller.
/// </summary>
internal enum LargeMessageKind : byte
{
    Argument = 0,
    Result = 1,
}

/// <summary>
/// Why a large message stream was aborted.
/// </summary>
internal enum LargeMessageAbortReason : byte
{
    /// <summary>The receiver has no budget for it right now.</summary>
    Busy = 0,

    /// <summary>The value is larger than the receiver accepts.</summary>
    TooLarge = 1,

    /// <summary>The sender canceled it.</summary>
    Cancelled = 2,

    /// <summary>Acknowledges the other side's abort.</summary>
    Ack = 3,
}

/// <summary>
/// Starts a large message: the inner message's body follows in <see cref="MessageType.MessageChunk"/> frames.
/// Body: <c>[invocationId (uint16), kind (uint8), innerType (uint8), totalLength (uint32)]</c>.
/// </summary>
internal partial class MessageStartMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.MessageStart;

    private IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public ushort InvocationId { get; set; }

    public LargeMessageKind Kind { get; set; }

    /// <summary>
    /// Type of the message the chunks carry: <see cref="MessageType.Invocation"/> or
    /// <see cref="MessageType.InvocationResult"/>.
    /// </summary>
    public MessageType InnerType { get; set; }

    /// <summary>
    /// Length of the inner message's body; the chunks' payloads add up to it.
    /// </summary>
    public uint TotalLength { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write(InvocationId);
        writer.Write((byte)Kind);
        writer.Write((byte)InnerType);
        writer.Write(TotalLength);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(4);
        InvocationId = reader.ReadUInt16();
        Kind = (LargeMessageKind)reader.ReadByte();
        InnerType = (MessageType)reader.ReadByte();
        TotalLength = reader.ReadUInt32();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}

/// <summary>
/// Aborts a large message stream, or acknowledges the other side's abort.
/// Body: <c>[invocationId (uint16), kind (uint8), reason (uint8)]</c>.
/// </summary>
internal partial class MessageAbortMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.MessageAbort;

    private IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public ushort InvocationId { get; set; }

    public LargeMessageKind Kind { get; set; }

    public LargeMessageAbortReason Reason { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(3);
        writer.Write(InvocationId);
        writer.Write((byte)Kind);
        writer.Write((byte)Reason);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(3);
        InvocationId = reader.ReadUInt16();
        Kind = (LargeMessageKind)reader.ReadByte();
        Reason = (LargeMessageAbortReason)reader.ReadByte();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
