using MemoryPack;
using NexNet.Pipes.Channels;

namespace NexNet.IntegrationTests.Pipes.Channels;


abstract class NetworkMessageUnion : INexusPooledMessageUnion<NetworkMessageUnion>
{
    public static void RegisterMessages(INexusUnionBuilder<NetworkMessageUnion> registerer)
    {
        registerer.Add<LoginMessage>();
        registerer.Add<ChatMessage>();
        registerer.Add<DisconnectMessage>();
    }
}

[MemoryPackable]
partial class LoginMessage : NetworkMessageUnion, INexusPooledMessage<LoginMessage>
{
    public static byte UnionId => 0;
    
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[MemoryPackable]
partial class ChatMessage : NetworkMessageUnion, INexusPooledMessage<ChatMessage>
{
    public static byte UnionId => 1;
    
    public string Sender { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long Timestamp { get; set; }
}

[MemoryPackable]
partial class DisconnectMessage : NetworkMessageUnion, INexusPooledMessage<DisconnectMessage>
{
    public static byte UnionId => 2;
    public string Reason { get; set; } = string.Empty;
    public int ErrorCode { get; set; }
}

[MemoryPackable]
partial class StandAloneMessage : NexusBasePooledMessage<StandAloneMessage>
{
    public string Reason { get; set; } = string.Empty;
    public int ErrorCode { get; set; }
}
