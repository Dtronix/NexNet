﻿using System;
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
    public string? Version { get; set; }

    [MemoryPackOrder(1)]
    public int ServerNexusHash { get; set; }
    
    [MemoryPackOrder(2)]
    public int ClientNexusHash { get; set; }

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
