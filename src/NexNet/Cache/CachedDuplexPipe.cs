using System.Collections.Concurrent;
using NexNet.Internals;
using NexNet.Pipes;

namespace NexNet.Cache;

internal class CachedDuplexPipe
{
    private readonly ConcurrentBag<NexusDuplexPipe> _cache = new();

    public NexusDuplexPipe Rent(INexusSession session, byte initialId)
    {
        if (!_cache.TryTake(out var cachedPipe))
            cachedPipe = new NexusDuplexPipe();

        cachedPipe.IsInCached = false;
        cachedPipe.Setup(initialId, session);

        return cachedPipe;
    }

    public void Return(NexusDuplexPipe pipe)
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
