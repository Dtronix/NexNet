using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ServerGreetingMessage : IMessageBase
{
    public static MessageType Type => MessageType.ServerGreeting;

    [MemoryPackOrder(0)]
    public int Version { get; set; }

    public void Reset()
    {
        // Noop
    }

}
