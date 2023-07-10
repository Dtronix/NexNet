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
    //public readonly CachedDeserializer<InvocationResultMessage> InvocationProxyResultDeserializer = new();
    //public readonly CachedDeserializer<InvocationCancellationMessage> InvocationCancellationRequestDeserializer = new();
    //public readonly CachedDeserializer<PipeCompleteMessage> PipeCompleteMessageDeserializer = new();
    //public readonly CachedDeserializer<PipeReadyMessage> PipeReadyMessageDeserializer = new();
    //public readonly CachedDeserializer<InvocationMessage> InvocationRequestDeserializer = new();
    //public readonly CachedDeserializer<ClientGreetingMessage> ClientGreetingDeserializer = new();
    //public readonly CachedDeserializer<ServerGreetingMessage> ServerGreetingDeserializer = new();
    public readonly CachedResettableItem<RegisteredInvocationState> RegisteredInvocationStateCache = new();
    public readonly CachedCts CancellationTokenSourceCache = new();
    public readonly CachedPipe NexusPipeCache = new();

    private readonly ICachedDeserializer?[] _cachedMessageDeserializers = new ICachedDeserializer?[50];

    public CacheManager()
    {
        _cachedMessageDeserializers[((int)ClientGreetingMessage.Type - 100)] =
            new CachedDeserializer<ClientGreetingMessage>();

        _cachedMessageDeserializers[((int)ServerGreetingMessage.Type - 100)] =
            new CachedDeserializer<ServerGreetingMessage>();

        _cachedMessageDeserializers[((int)InvocationMessage.Type - 100)] =
            new CachedDeserializer<InvocationMessage>();

        _cachedMessageDeserializers[((int)InvocationCancellationMessage.Type - 100)] =
            new CachedDeserializer<InvocationCancellationMessage>();

        _cachedMessageDeserializers[((int)InvocationResultMessage.Type - 100)] =
            new CachedDeserializer<InvocationResultMessage>();

        _cachedMessageDeserializers[((int)PipeReadyMessage.Type - 100)] =
            new CachedDeserializer<PipeReadyMessage>();

        _cachedMessageDeserializers[((int)PipeCompleteMessage.Type - 100)] =
            new CachedDeserializer<PipeCompleteMessage>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CachedDeserializer<T> Cache<T>()
        where T : IMessageBase, new()
    {
        // Offset the messages -100 for a smaller array.
        return Unsafe.As<CachedDeserializer<T>>(_cachedMessageDeserializers[((int)T.Type) - 100])!;

        /*
        // If the cache is null, then we need to create a cache.
        if (cache == null)
        {
            cache = new CachedDeserializer<T>();

            // Switch the values in teh array if it is null.
            var originalValue = Interlocked.CompareExchange(ref _cachedMessageDeserializers[typeIndex], cache, null);
            
            // If the original value was not null, then a cache was already assigned to the array
            // and we need to return that one instead of the newly created cache.
            if(originalValue != null)
                cache = originalValue;

        }

        return Unsafe.As<CachedDeserializer<T>>(cache)!;*/
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
        {
            _cachedMessageDeserializers[i]?.Clear();
        }

        RegisteredInvocationStateCache.Clear();
        CancellationTokenSourceCache.Clear();
        NexusPipeCache.Clear();
    }
}
