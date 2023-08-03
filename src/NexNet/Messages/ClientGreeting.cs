using System;
using MemoryPack;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ClientGreetingMessage : IMessageBase
{
    private bool _isArgumentPoolArray;
    public static MessageType Type { get; } = MessageType.ClientGreeting;

    [MemoryPackOrder(0)]
    public int Version { get; set; }

    /// <summary>
    /// This is the hash of the server's methods.  If this does not match the server's hash,
    /// then the server and client method invocations are out of sync.
    /// </summary>
    [MemoryPackOrder(1)]
    public int ServerNexusMethodHash { get; set; }

    /// <summary>
    /// This is the hash of the client's methods.  If this does not match the server's hash,
    /// then the server and client method invocations are out of sync.
    /// </summary>
    [MemoryPackOrder(2)]
    public int ClientNexusMethodHash { get; set; }

    /// <summary>
    /// (Optional) Token to be passed to the server upon connection for validation.
    /// </summary>
    [MemoryPackOrder(3)]
    [MemoryPoolFormatter<byte>]
    public Memory<byte> AuthenticationToken { get; set; }
    

    [MemoryPackOnDeserialized]
    private void OnDeserialized()
    {
        _isArgumentPoolArray = true;
    }

    public void Reset()
    {
        if (!_isArgumentPoolArray)
            return;

        // Reset the pool flag.
        _isArgumentPoolArray = false;

        if (AuthenticationToken.IsEmpty)
            return;

        IMessageBase.Return(AuthenticationToken);
        AuthenticationToken = default;
    }
}
