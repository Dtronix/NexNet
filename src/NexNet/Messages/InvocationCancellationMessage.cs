using System.Threading;
using MemoryPack;
using NexNet.Pools;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class InvocationCancellationMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.InvocationCancellation;

    internal IPooledMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public IPooledMessage? MessageCache
    {
        set => _messageCache = value;
    }

    [MemoryPackOrder(0)]
    public int InvocationId { get; set; }

    [MemoryPackConstructor]
    public InvocationCancellationMessage()
    {
        
    }

    public InvocationCancellationMessage(int invocationId)
    {
        InvocationId = invocationId;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
