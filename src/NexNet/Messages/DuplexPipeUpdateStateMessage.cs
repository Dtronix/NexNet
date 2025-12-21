using System.Threading;
using MemoryPack;
using NexNet.Pipes;
using NexNet.Pools;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class DuplexPipeUpdateStateMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.DuplexPipeUpdateState;

    private IPooledMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public IPooledMessage? MessageCache
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
