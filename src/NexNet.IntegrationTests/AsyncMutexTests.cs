using NexNet.Internals.Threading;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class AsyncMutexTests
{
    [Test]
    public void TryEnter_SucceedsOnlyWhenFree()
    {
        var mutex = new AsyncMutex();
        var first = mutex.TryEnter();
        Assert.That(first.Success, Is.True);
        Assert.That(mutex.IsAvailable, Is.False);
        Assert.That(mutex.TryEnter().Success, Is.False);

        first.Dispose();
        Assert.That(mutex.IsAvailable, Is.True);
        using var second = mutex.TryEnter();
        Assert.That(second.Success, Is.True);
    }

    [Test]
    public async Task EnterAsync_CompletesSynchronouslyWhenFree()
    {
        var mutex = new AsyncMutex();
        var enter = mutex.EnterAsync();
        Assert.That(enter.IsCompletedSuccessfully, Is.True);
        using var token = await enter;
        Assert.That(token.Success, Is.True);
    }

    [Test]
    public async Task Release_HandsTheLockToWaitersInQueueOrder()
    {
        var mutex = new AsyncMutex();
        var held = mutex.TryEnter();
        var first = mutex.EnterAsync().AsTask();
        var second = mutex.EnterAsync().AsTask();

        held.Dispose();
        var firstToken = await first.Timeout(1);
        await Task.Delay(50);
        Assert.That(second.IsCompleted, Is.False);

        // Handed over, not freed: nobody can barge in ahead of the queue.
        Assert.That(mutex.TryEnter().Success, Is.False);

        firstToken.Dispose();
        (await second.Timeout(1)).Dispose();
        Assert.That(mutex.IsAvailable, Is.True);
    }

    [Test]
    public async Task EnterAsync_Canceled_ThrowsTaskCanceledAndLeavesTheQueue()
    {
        var mutex = new AsyncMutex();
        var held = mutex.TryEnter();
        using var cts = new CancellationTokenSource();
        var canceled = mutex.EnterAsync(cts.Token).AsTask();
        var next = mutex.EnterAsync().AsTask();

        await cts.CancelAsync();
        Assert.That(async () => await canceled.Timeout(1), Throws.InstanceOf<TaskCanceledException>());

        held.Dispose();
        using var nextToken = await next.Timeout(1);
        Assert.That(nextToken.Success, Is.True);
    }

    [Test]
    public void EnterAsync_AlreadyCanceledToken_ThrowsTaskCanceled()
    {
        var mutex = new AsyncMutex();
        Assert.That(async () => await mutex.EnterAsync(new CancellationToken(true)),
            Throws.InstanceOf<TaskCanceledException>());
        Assert.That(mutex.IsAvailable, Is.True);
    }

    [Test]
    public void Dispose_Twice_Throws()
    {
        var mutex = new AsyncMutex();
        var token = mutex.TryEnter();
        token.Dispose();
        Assert.Throws<InvalidOperationException>(() => token.Dispose());
    }

    [Test]
    public void Dispose_StaleToken_DoesNotReleaseTheNextHolder()
    {
        var mutex = new AsyncMutex();
        var stale = mutex.TryEnter();
        stale.Dispose();
        using var current = mutex.TryEnter();

        Assert.Throws<InvalidOperationException>(() => stale.Dispose());
        Assert.That(mutex.IsAvailable, Is.False);
    }

    [Test]
    public void Dispose_DefaultToken_DoesNothing()
    {
        AsyncMutex.LockToken token = default;
        Assert.That(token.Success, Is.False);
        Assert.DoesNotThrow(() => token.Dispose());
    }

    [Test]
    public async Task Waiters_AreReusedAcrossManyWaits()
    {
        var mutex = new AsyncMutex();
        for (var i = 0; i < 10_000; i++)
        {
            var held = mutex.TryEnter();
            var wait = mutex.EnterAsync();
            held.Dispose();
            (await wait).Dispose();
        }

        Assert.That(mutex.IsAvailable, Is.True);
    }

    [Test]
    public async Task ConcurrentHolders_AreMutuallyExclusive()
    {
        const int Workers = 16;
        const int PerWorker = 2_000;
        var mutex = new AsyncMutex();
        var inside = 0;
        var maxInside = 0;
        long counter = 0;

        var workers = Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                // Some waits carry a cancelable token that is never canceled, to exercise registration cleanup.
                using var cts = (i & 1) == 0 ? new CancellationTokenSource() : null;
                AsyncMutex.LockToken token;
                if ((i % 7) == 0 && (token = mutex.TryEnter()).Success)
                {
                }
                else
                {
                    token = await mutex.EnterAsync(cts?.Token ?? CancellationToken.None);
                }

                using (token)
                {
                    var now = Interlocked.Increment(ref inside);
                    if (now > Volatile.Read(ref maxInside))
                        Volatile.Write(ref maxInside, now);
                    counter++; // not atomic: only correct under mutual exclusion
                    if ((i & 15) == 0)
                        await Task.Yield();
                    Interlocked.Decrement(ref inside);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers).Timeout(30);
        Assert.That(maxInside, Is.EqualTo(1));
        Assert.That(counter, Is.EqualTo(Workers * PerWorker));
        Assert.That(mutex.IsAvailable, Is.True);
    }
}
