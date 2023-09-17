using System.Threading;
using MemoryPack;
using NexNet.Cache;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ServerGreetingMessage : IMessageBase
{
    public static MessageType Type => MessageType.ServerGreeting;

    private ICachedMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public ICachedMessage? MessageCache
    {
        set => _messageCache = value;
    }

    [MemoryPackOrder(0)]
    public int Version { get; set; }

    [MemoryPackOrder(1)]
    public long ClientId { get; set; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
