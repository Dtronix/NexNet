using NexNet.Internals.Threading;
using System.Threading.Tasks;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class AsyncMutexTests
{
    [Test]
    public async Task TryEnter_SucceedsOnlyWhenFree()
    {
        var mutex = new AsyncMutex();
        var first = mutex.TryEnter();
        await Assert.That(first.Success).IsTrue();
        await Assert.That(mutex.IsAvailable).IsFalse();
        await Assert.That(mutex.TryEnter().Success).IsFalse();

        first.Dispose();
        await Assert.That(mutex.IsAvailable).IsTrue();
        using var second = mutex.TryEnter();
        await Assert.That(second.Success).IsTrue();
    }

    [Test]
    public async Task EnterAsync_CompletesSynchronouslyWhenFree()
    {
        var mutex = new AsyncMutex();
        var enter = mutex.EnterAsync();
        await Assert.That(enter.IsCompletedSuccessfully).IsTrue();
        using var token = await enter;
        await Assert.That(token.Success).IsTrue();
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
        await Assert.That(second.IsCompleted).IsFalse();

        // Handed over, not freed: nobody can barge in ahead of the queue.
        await Assert.That(mutex.TryEnter().Success).IsFalse();

        firstToken.Dispose();
        (await second.Timeout(1)).Dispose();
        await Assert.That(mutex.IsAvailable).IsTrue();
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
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await canceled.Timeout(1));

        held.Dispose();
        using var nextToken = await next.Timeout(1);
        await Assert.That(nextToken.Success).IsTrue();
    }

    [Test]
    public async Task EnterAsync_AlreadyCanceledToken_ThrowsTaskCanceled()
    {
        var mutex = new AsyncMutex();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await mutex.EnterAsync(new CancellationToken(true)));
        await Assert.That(mutex.IsAvailable).IsTrue();
    }

    [Test]
    public async Task Dispose_Twice_Throws()
    {
        var mutex = new AsyncMutex();
        var token = mutex.TryEnter();
        token.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => token.Dispose());
    }

    [Test]
    public async Task Dispose_StaleToken_DoesNotReleaseTheNextHolder()
    {
        var mutex = new AsyncMutex();
        var stale = mutex.TryEnter();
        stale.Dispose();
        using var current = mutex.TryEnter();

        Assert.ThrowsExactly<InvalidOperationException>(() => stale.Dispose());
        await Assert.That(mutex.IsAvailable).IsFalse();
    }

    [Test]
    public async Task Dispose_DefaultToken_DoesNothing()
    {
        AsyncMutex.LockToken token = default;
        await Assert.That(token.Success).IsFalse();
        await Assert.That(() => token.Dispose()).ThrowsNothing();
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

        await Assert.That(mutex.IsAvailable).IsTrue();
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
        await Assert.That(maxInside).IsEqualTo(1);
        await Assert.That(counter).IsEqualTo(Workers * PerWorker);
        await Assert.That(mutex.IsAvailable).IsTrue();
    }
}