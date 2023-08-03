using System.Collections.Concurrent;
using NexNet.Internals;
using NexNet.Internals.Pipes;

namespace NexNet.Cache;

internal class CachedPipeManager
{
    private readonly ConcurrentBag<NexusPipeManager> _cache = new();

    public NexusPipeManager Rent(INexusSession session)
    {
        if (!_cache.TryTake(out var cachedItem))
            cachedItem = new NexusPipeManager();

        cachedItem.Setup(session);

        return cachedItem;
    }

    public void Return(NexusPipeManager item)
    {
        _cache.Add(item);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
