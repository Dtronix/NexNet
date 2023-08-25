using System.Threading;
using MemoryPack;
using NexNet.Cache;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class InvocationCancellationMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.InvocationCancellation;

    private ICachedMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public ICachedMessage? MessageCache
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
        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        cache.Return(this);
    }
}
