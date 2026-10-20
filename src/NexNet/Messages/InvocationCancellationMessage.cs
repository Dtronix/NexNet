using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Requests cancellation of an in-flight invocation.
/// Body: <c>[invocationId (uint16)]</c>.
/// </summary>
internal partial class InvocationCancellationMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.InvocationCancellation;

    internal IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public ushort InvocationId { get; set; }

    public InvocationCancellationMessage()
    {

    }

    public InvocationCancellationMessage(ushort invocationId)
    {
        InvocationId = invocationId;
    }

    public void Serialize(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(1);
        writer.Write(InvocationId);
    }

    public void Deserialize(ref MsgPackReader reader)
    {
        reader.ReadArrayHeader(1);
        InvocationId = reader.ReadUInt16();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
