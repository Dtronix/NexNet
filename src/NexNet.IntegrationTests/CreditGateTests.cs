using NexNet.Internals;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class CreditGateTests
{
    private static readonly TimeSpan Infinite = Timeout.InfiniteTimeSpan;

    [Test]
    public void TryTake_TakesOnlyAvailableCredits()
    {
        var gate = new CreditGate();
        Assert.That(gate.TryTake(), Is.False);

        gate.Release(2);
        Assert.That(gate.CurrentCount, Is.EqualTo(2));
        Assert.That(gate.TryTake(), Is.True);
        Assert.That(gate.TryTake(), Is.True);
        Assert.That(gate.TryTake(), Is.False);
    }

    [Test]
    public async Task WaitAsync_CompletesSynchronouslyWhenCreditIsAvailable()
    {
        var gate = new CreditGate();
        gate.Release();

        var wait = gate.WaitAsync(Infinite, CancellationToken.None);
        Assert.That(wait.IsCompleted, Is.True);
        Assert.That(await wait, Is.EqualTo(CreditWaitResult.Acquired));
    }

    [Test]
    public async Task Release_ServesWaitersBeforeMakingCreditAvailable()
    {
        var gate = new CreditGate();
        var waits = Enumerable.Range(0, 5)
            .Select(_ => gate.WaitAsync(Infinite, CancellationToken.None).AsTask())
            .ToArray();

        gate.Release(7);

        Assert.That(await Task.WhenAll(waits).Timeout(1), Has.All.EqualTo(CreditWaitResult.Acquired));
        Assert.That(gate.CurrentCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Release_HandsCreditsToWaitersInQueueOrder()
    {
        var gate = new CreditGate();
        var first = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();
        var second = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();

        gate.Release();
        Assert.That(await first.Timeout(1), Is.EqualTo(CreditWaitResult.Acquired));
        await Task.Delay(50);
        Assert.That(second.IsCompleted, Is.False);

        gate.Release();
        Assert.That(await second.Timeout(1), Is.EqualTo(CreditWaitResult.Acquired));
    }

    [Test]
    public async Task WaitAsync_TimesOut()
    {
        var gate = new CreditGate();
        var result = await gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).AsTask().Timeout(1);
        Assert.That(result, Is.EqualTo(CreditWaitResult.TimedOut));

        // The timed-out waiter no longer takes credit.
        gate.Release();
        Assert.That(gate.CurrentCount, Is.EqualTo(1));
    }

    [Test]
    public void WaitAsync_ZeroTimeout_DoesNotPark()
    {
        var gate = new CreditGate();
        var wait = gate.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.That(wait.IsCompleted, Is.True);
        Assert.That(wait.Result, Is.EqualTo(CreditWaitResult.TimedOut));
    }

    [Test]
    public async Task WaitAsync_Canceled_ThrowsAndLeavesTheQueue()
    {
        var gate = new CreditGate();
        using var cts = new CancellationTokenSource();
        var canceled = gate.WaitAsync(Infinite, cts.Token).AsTask();
        var next = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();

        await cts.CancelAsync();
        Assert.That(async () => await canceled.Timeout(1), Throws.InstanceOf<OperationCanceledException>());

        // The credit goes to the next waiter, not the canceled one.
        gate.Release();
        Assert.That(await next.Timeout(1), Is.EqualTo(CreditWaitResult.Acquired));
        Assert.That(gate.CurrentCount, Is.Zero);
    }

    [Test]
    public void WaitAsync_AlreadyCanceledToken_Throws()
    {
        var gate = new CreditGate();
        var wait = gate.WaitAsync(Infinite, new CancellationToken(true));
        Assert.That(wait.IsCanceled, Is.True);
    }

    [Test]
    public async Task Close_EndsParkedAndLaterWaits()
    {
        var gate = new CreditGate();
        var parked = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();

        gate.Close();
        Assert.That(await parked.Timeout(1), Is.EqualTo(CreditWaitResult.Closed));

        var later = gate.WaitAsync(Infinite, CancellationToken.None);
        Assert.That(later.IsCompleted, Is.True);
        Assert.That(later.Result, Is.EqualTo(CreditWaitResult.Closed));
    }

    [Test]
    public async Task Waiters_AreReusedAcrossManyWaits()
    {
        var gate = new CreditGate();
        for (var i = 0; i < 10_000; i++)
        {
            var wait = gate.WaitAsync(Infinite, CancellationToken.None);
            gate.Release();
            Assert.That(await wait, Is.EqualTo(CreditWaitResult.Acquired));
        }

        Assert.That(gate.CurrentCount, Is.Zero);
    }

    [Test]
    public async Task ConcurrentWaitersAndReleases_AccountForEveryCredit()
    {
        const int Workers = 16;
        const int PerWorker = 2_000;
        var gate = new CreditGate();
        var acquired = 0;

        var workers = Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < PerWorker; i++)
            {
                // Some waits carry a cancelable token that is never canceled, to exercise registration cleanup.
                using var cts = (i & 1) == 0 ? new CancellationTokenSource() : null;
                if (await gate.WaitAsync(Infinite, cts?.Token ?? CancellationToken.None) == CreditWaitResult.Acquired)
                    Interlocked.Increment(ref acquired);
            }
        })).ToArray();

        var releaser = Task.Run(async () =>
        {
            for (var i = 0; i < Workers * PerWorker; i += 10)
            {
                gate.Release(10);
                if (i % 1000 == 0)
                    await Task.Yield();
            }
        });

        await Task.WhenAll(workers.Append(releaser)).Timeout(30);
        Assert.That(acquired, Is.EqualTo(Workers * PerWorker));
        Assert.That(gate.CurrentCount, Is.Zero);
    }
}
