using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class InvocationCancellationMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.InvocationCancellation;

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
    public void Reset()
    {
        // Noop
    }
}
