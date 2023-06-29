using System.Collections.Concurrent;
using System.Threading;

namespace NexNet.Cache;

internal class CachedPipe
{
    private readonly ConcurrentBag<NexNetPipe> _cache = new();

    public NexNetPipe Rent()
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new NexNetPipe(true);

        return cachedItem;
    }

    public void Return(NexNetPipe item)
    {
        item.Reset();
        _cache.Add(item);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
