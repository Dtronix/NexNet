using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MemoryPack;
using NexNet.Messages;

namespace NexNet.Cache;

internal class CachedDeserializer<T> : ICachedDeserializer
    where T : IMessageBase, new()
{
    private readonly ConcurrentBag<T> _cache = new();

    public T Rent()
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T();

        return cachedItem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T? Deserialize(in ReadOnlySequence<byte> bodySequence)
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T();

        MemoryPackSerializer.Deserialize(bodySequence, ref cachedItem);

        return cachedItem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IMessageBase DeserializeInterface(in ReadOnlySequence<byte> bodySequence)
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T();

        MemoryPackSerializer.Deserialize(bodySequence, ref cachedItem);

        return cachedItem!;
    }

    public void Return(T item)
    {
        item.Reset();
        _cache.Add(item);

    }

    public void Clear()
    {
        _cache.Clear();
    }
}

internal interface ICachedDeserializer
{
    void Clear();
    IMessageBase DeserializeInterface(in ReadOnlySequence<byte> bodySequence);
}
