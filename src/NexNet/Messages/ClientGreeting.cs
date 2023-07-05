using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable]
internal partial class ClientGreetingMessage : IMessageBodyBase
{
    public static MessageType Type { get; } = MessageType.GreetingClient;

    public int Version { get; set; }

    /// <summary>
    /// This is the hash of the server's methods.  If this does not match the server's hash,
    /// then the server and client method invocations are out of sync.
    /// </summary>
    public int ServerNexusMethodHash { get; set; }

    /// <summary>
    /// This is the hash of the client's methods.  If this does not match the server's hash,
    /// then the server and client method invocations are out of sync.
    /// </summary>
    public int ClientNexusMethodHash { get; set; }

    /// <summary>
    /// (Optional) Token to be passed to the server upon connection for validation.
    /// </summary>
    public byte[]? AuthenticationToken { get; set; }
}
