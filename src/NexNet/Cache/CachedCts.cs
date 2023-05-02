using System.Collections.Concurrent;
using System.Threading;

namespace NexNet.Cache;

internal class CachedCts
{
    private readonly ConcurrentBag<CancellationTokenSource> _cache = new();

    public CancellationTokenSource Rent()
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new CancellationTokenSource();

        return cachedItem;
    }

    public void Return(CancellationTokenSource item)
    {
        if (item.TryReset())
            _cache.Add(item);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
