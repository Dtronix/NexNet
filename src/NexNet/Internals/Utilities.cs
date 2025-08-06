﻿using System;
using System.Runtime.CompilerServices;
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
    public static void TryReleaseSemaphore(SemaphoreSlim? semaphore)
    {
        if (semaphore?.CurrentCount == 0)
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
    
    public static async ValueTask<IDisposable> WaitDisposableAsync(this SemaphoreSlim semaphore)
    {
        ArgumentNullException.ThrowIfNull(semaphore);
        await semaphore.WaitAsync().ConfigureAwait(false);
        return new SemaphoreSlimDisposable(semaphore);
    }
    
    public static IDisposable WaitDisposable(this SemaphoreSlim semaphore)
    {
        ArgumentNullException.ThrowIfNull(semaphore);
        semaphore.Wait();
        return new SemaphoreSlimDisposable(semaphore);
    }
    
    private readonly struct SemaphoreSlimDisposable(SemaphoreSlim semaphore) : IDisposable
    { 
        public void Dispose()
        {
            semaphore.Release();
        }
    }
}
