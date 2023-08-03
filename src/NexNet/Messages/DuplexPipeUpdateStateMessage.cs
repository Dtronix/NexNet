using MemoryPack;
using NexNet.Internals.Pipes;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class DuplexPipeUpdateStateMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.DuplexPipeUpdateState;

    [MemoryPackOrder(0)]
    public ushort PipeId { get; set; }

    [MemoryPackOrder(1)]
    public NexusDuplexPipe.State State { get; set; }

    public void Reset()
    {
        // Noop
    }
}
