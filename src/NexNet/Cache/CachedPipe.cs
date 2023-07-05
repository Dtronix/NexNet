using System.Collections.Concurrent;
using System.Threading;

namespace NexNet.Cache;

internal class CachedPipe
{
    private readonly ConcurrentBag<NexusPipe> _cache = new();

    public NexusPipe Rent()
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new NexusPipe();

        return cachedItem;
    }

    public void Return(NexusPipe item)
    {
        item.Reset();
        _cache.Add(item);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
