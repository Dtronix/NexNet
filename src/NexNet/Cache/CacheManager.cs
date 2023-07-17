using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NexNet.Internals;
using NexNet.Messages;

namespace NexNet.Cache;

internal class CacheManager
{
    public readonly CachedResettableItem<RegisteredInvocationState> RegisteredInvocationStateCache = new();
    public readonly CachedPipeManager PipeManagerCache = new();
    public readonly CachedCts CancellationTokenSourceCache = new();
    public readonly CachedDuplexPipe NexusDuplexPipeCache = new();

    private readonly ICachedDeserializer?[] _cachedMessageDeserializers = new ICachedDeserializer?[50];

    public CacheManager()
    {
        // This is an integer modifier to reduce the maximum array needed.
        const int modifier = 100;

        _cachedMessageDeserializers[((int)ClientGreetingMessage.Type - modifier)] =
            new CachedDeserializer<ClientGreetingMessage>();

        _cachedMessageDeserializers[((int)ServerGreetingMessage.Type - modifier)] =
            new CachedDeserializer<ServerGreetingMessage>();

        _cachedMessageDeserializers[((int)InvocationMessage.Type - modifier)] =
            new CachedDeserializer<InvocationMessage>();

        _cachedMessageDeserializers[((int)InvocationCancellationMessage.Type - modifier)] =
            new CachedDeserializer<InvocationCancellationMessage>();

        _cachedMessageDeserializers[((int)InvocationResultMessage.Type - modifier)] =
            new CachedDeserializer<InvocationResultMessage>();

        _cachedMessageDeserializers[((int)DuplexPipeUpdateStateMessage.Type - modifier)] =
            new CachedDeserializer<DuplexPipeUpdateStateMessage>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CachedDeserializer<T> Cache<T>()
        where T : IMessageBase, new()
    {
        // Offset the messages -100 for a smaller array.
        return Unsafe.As<CachedDeserializer<T>>(_cachedMessageDeserializers[((int)T.Type) - 100])!;
    }

    public ICachedDeserializer Cache(MessageType type)
    {
        return _cachedMessageDeserializers[((int)type) - 100]!;
    }

    public T Rent<T>()
        where T : IMessageBase, new()
    {
        return Cache<T>().Rent();
    }

    public void Return<T>(T message)
        where T : IMessageBase, new()
    {
        Cache<T>().Return(message);
    }

    public IMessageBase Deserialize(MessageType type, ReadOnlySequence<byte> sequence)
    {
        return _cachedMessageDeserializers[((int)type) - 100]!.DeserializeInterface(sequence);
    }

    public virtual void Clear()
    {
        for (int i = 0; i < _cachedMessageDeserializers.Length; i++)
            _cachedMessageDeserializers[i]?.Clear();

        RegisteredInvocationStateCache.Clear();
        CancellationTokenSourceCache.Clear();
        PipeManagerCache.Clear();
    }
}
