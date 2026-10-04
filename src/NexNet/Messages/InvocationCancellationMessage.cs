using System.Threading;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Requests cancellation of an in-flight invocation.
/// Body: <c>[invocationId (int)]</c>.
/// </summary>
internal partial class InvocationCancellationMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.InvocationCancellation;

    internal IPooledMessage? _messageCache = null!;

    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    public int InvocationId { get; set; }

    public InvocationCancellationMessage()
    {

    }

    public InvocationCancellationMessage(int invocationId)
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
        InvocationId = reader.ReadInt32();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
