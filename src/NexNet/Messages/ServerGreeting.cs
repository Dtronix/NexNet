using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class ServerGreetingMessage : IMessageBase
{
    public static MessageType Type => MessageType.ServerGreeting;

    public int Version { get; set; }

}
