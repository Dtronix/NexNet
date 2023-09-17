using System.Threading;
using MemoryPack;
using NexNet.Cache;
using NexNet.Pipes;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class DuplexPipeUpdateStateMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.DuplexPipeUpdateState;

    private ICachedMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public ICachedMessage? MessageCache
    {
        set => _messageCache = value;
    }

    [MemoryPackOrder(0)]
    public ushort PipeId { get; set; }

    [MemoryPackOrder(1)]
    public NexusDuplexPipe.State State { get; set; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _messageCache, null)?.Return(this);
    }
}
