using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
public partial class ServerGreetingMessage : IMessageBodyBase
{
    public static MessageType Type => MessageType.GreetingServer;

    public int Version { get; set; }

}
