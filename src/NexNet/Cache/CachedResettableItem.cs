using System.Collections.Concurrent;

namespace NexNet.Cache;

internal class CachedResettableItem<T>
    where T : IResettable, new()
{
    private readonly ConcurrentBag<T> _cache = new();

    public T Rent()
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T();

        return cachedItem;
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
