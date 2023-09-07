using System.Collections.Concurrent;
using NexNet.Internals;
using NexNet.Pipes;

namespace NexNet.Cache;

internal class CachedRentedDuplexPipe
{
    private readonly ConcurrentBag<RentedNexusDuplexPipe> _cache = new();

    public RentedNexusDuplexPipe Rent(INexusSession session, byte initialId)
    {
        if (!_cache.TryTake(out var cachedPipe))
            cachedPipe = new RentedNexusDuplexPipe();

        cachedPipe.IsInCached = false;
        cachedPipe.Setup(initialId, session);

        return cachedPipe;
    }

    public void Return(RentedNexusDuplexPipe pipe)
    {
        if(pipe.IsInCached)
            return;

        pipe.IsInCached = true;
        pipe.Reset();
        _cache.Add(pipe);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
