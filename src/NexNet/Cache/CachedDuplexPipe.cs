using System.Collections.Concurrent;
using System.Threading;
using NexNet.Internals;

namespace NexNet.Cache;

internal class CachedDuplexPipe
{
    private readonly ConcurrentBag<NexusDuplexPipe> _cache = new();

    public NexusDuplexPipe Rent(INexusSession session, byte initialId)
    {
        if (!_cache.TryTake(out var cachedPipe))
            cachedPipe = new NexusDuplexPipe();

        cachedPipe.Setup(initialId, session);

        return cachedPipe;
    }

    public void Return(NexusDuplexPipe pipe)
    {
        pipe.Reset();
        _cache.Add(pipe);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
