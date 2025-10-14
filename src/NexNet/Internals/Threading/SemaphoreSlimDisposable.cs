using System;
using System.Threading;

namespace NexNet.Internals.Threading;

internal readonly struct SemaphoreSlimDisposable(SemaphoreSlim semaphore) : IDisposable
{ 
    public void Dispose()
    {
        try
        {
            semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // ignore disposals.
        }

    }
}
