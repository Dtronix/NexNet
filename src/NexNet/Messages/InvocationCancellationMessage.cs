using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class InvocationCancellationMessage : IMessageBase
{
    public static MessageType Type { get; } = MessageType.InvocationCancellation;

    public int InvocationId { get; set; }

    [MemoryPackConstructor]
    public InvocationCancellationMessage()
    {
        
    }

    public InvocationCancellationMessage(int invocationId)
    {
        InvocationId = invocationId;
    }
}
