using System;
using System.Threading;
using MemoryPack;
using NexNet.Cache;

namespace NexNet.Messages;

[MemoryPackable(SerializeLayout.Explicit)]
internal partial class ClientGreetingMessage : IClientGreetingMessageBase
{
    private bool _isArgumentPoolArray;
    public static MessageType Type { get; } = MessageType.ClientGreeting;

    private ICachedMessage? _messageCache = null!;

    [MemoryPackIgnore]
    public ICachedMessage? MessageCache
    {
        set => _messageCache = value;
    }

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

    public void Dispose()
    {
        var cache = Interlocked.Exchange(ref _messageCache, null);

        if (cache == null)
            return;

        if (_isArgumentPoolArray)
        {

            // Reset the pool flag.
            _isArgumentPoolArray = false;

            if (AuthenticationToken.IsEmpty)
                return;

            IMessageBase.ReturnMemoryPackMemory(AuthenticationToken);
            AuthenticationToken = default;
        }

        cache.Return(this);
    }
}
