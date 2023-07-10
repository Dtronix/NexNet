using System.Collections.Concurrent;
using System.Threading;
using NexNet.Internals;

namespace NexNet.Cache;

internal class CachedPipe
{
    private readonly ConcurrentBag<NexusPipe> _cache = new();

    public NexusPipe Rent(INexusSession session, int invocationId)
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new NexusPipe();

        cachedItem.Session = session;
        cachedItem.InvocationId = invocationId;

        return cachedItem;
    }

    public void Return(NexusPipe item)
    {
        item.Reset();
        item.Session = null;
        item.InvocationId = -1;
        _cache.Add(item);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
