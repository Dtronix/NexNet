using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Internals;

internal static class Utilities
{
    /// <summary>
    /// Releases the semaphore if it is currently held.
    /// </summary>
    /// <param name="semaphore">The semaphore to release.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void TryReleaseSemaphore(SemaphoreSlim semaphore)
    {
        if (semaphore.CurrentCount == 0)
        {
            try
            {
                semaphore.Release();
            }
            catch
            {
                // ignore.
            }
        }
    }
}
