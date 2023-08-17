using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MemoryPack;
using NexNet.Messages;

namespace NexNet.Cache;

internal class CachedCachedMessage<T> : ICachedMessage
    where T : class, IMessageBase, new()
{
    private readonly ConcurrentBag<T> _cache = new();

    public T Rent()
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T() { MessageCache = this };

        return cachedItem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Deserialize(in ReadOnlySequence<byte> bodySequence)
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T() { MessageCache = this };

        MemoryPackSerializer.Deserialize(bodySequence, ref cachedItem);

        return cachedItem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IMessageBase DeserializeInterface(in ReadOnlySequence<byte> bodySequence)
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T() { MessageCache = this };

        MemoryPackSerializer.Deserialize(bodySequence, ref cachedItem);

        return cachedItem!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(IMessageBase item)
    {
        item.MessageCache = null;
        _cache.Add(Unsafe.As<T>(item));
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
