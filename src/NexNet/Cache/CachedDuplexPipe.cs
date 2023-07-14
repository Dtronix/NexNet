using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;

namespace NexNet.Cache;

internal class CachedDuplexPipe
{
    private readonly ConcurrentBag<NexusDuplexPipeSlim> _cache = new();

    public NexusDuplexPipeSlim Rent(INexusSession session, byte initialId, Func<INexusDuplexPipe, ValueTask>? onReady)
    {
        if (!_cache.TryTake(out var cachedPipe))
            cachedPipe = new NexusDuplexPipeSlim();

        cachedPipe.Setup(initialId, session, onReady);

        return cachedPipe;
    }

    public void Return(NexusDuplexPipeSlim pipe)
    {
        pipe.Reset();
        _cache.Add(pipe);
    }

    public void Clear()
    {
        _cache.Clear();
    }
}
