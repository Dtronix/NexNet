using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Returns invocation credit to the peer: it may queue <see cref="Credits"/> more invocations on this side.
/// Body: <c>[credits (uint16)]</c>.
/// </summary>
internal partial class InvocationCreditMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.InvocationCredit;

    private IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public ushort Credits { get; set; }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(1);
        writer.Write(Credits);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(1);
        Credits = reader.ReadUInt16();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
