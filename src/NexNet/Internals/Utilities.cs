using System.Runtime.CompilerServices;
using System.Threading;

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
