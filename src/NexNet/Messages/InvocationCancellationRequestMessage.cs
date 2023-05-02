using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
public partial class InvocationCancellationRequestMessage : IMessageBodyBase
{
    public static MessageType Type { get; } = MessageType.InvocationCancellationRequest;

    public int InvocationId { get; set; }

    [MemoryPackConstructor]
    public InvocationCancellationRequestMessage()
    {
        
    }

    public InvocationCancellationRequestMessage(int invocationId)
    {
        InvocationId = invocationId;
    }
}
