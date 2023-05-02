using System.Collections.Concurrent;

namespace NexNet.Cache;

internal class CachedItem<T>
    where T : new()
{
    private readonly ConcurrentBag<T> _cache = new();

    public T GetItem()
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new T();

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
