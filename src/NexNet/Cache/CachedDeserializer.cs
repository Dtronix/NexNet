using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace NexNet.Cache;

internal class CachedDeserializer<T>
    where T : new()
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

    public void Return(T item)
    {
        _cache.Add(item);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
