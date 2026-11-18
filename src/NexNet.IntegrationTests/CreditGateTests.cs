using NexNet.Internals;
using System.Threading.Tasks;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class CreditGateTests
{
    private static readonly TimeSpan Infinite = Timeout.InfiniteTimeSpan;

    [Test]
    public async Task TryTake_TakesOnlyAvailableCredits()
    {
        var gate = new CreditGate();
        await Assert.That(gate.TryTake()).IsFalse();

        gate.Release(2);
        await Assert.That(gate.CurrentCount).IsEqualTo(2);
        await Assert.That(gate.TryTake()).IsTrue();
        await Assert.That(gate.TryTake()).IsTrue();
        await Assert.That(gate.TryTake()).IsFalse();
    }

    [Test]
    public async Task WaitAsync_CompletesSynchronouslyWhenCreditIsAvailable()
    {
        var gate = new CreditGate();
        gate.Release();

        var wait = gate.WaitAsync(Infinite, CancellationToken.None);
        await Assert.That(wait.IsCompleted).IsTrue();
        await Assert.That(await wait).IsEqualTo(CreditWaitResult.Acquired);
    }

    [Test]
    public async Task Release_ServesWaitersBeforeMakingCreditAvailable()
    {
        var gate = new CreditGate();
        var waits = Enumerable.Range(0, 5)
            .Select(_ => gate.WaitAsync(Infinite, CancellationToken.None).AsTask())
            .ToArray();

        gate.Release(7);

        await Assert.That(await Task.WhenAll(waits).Timeout(1)).All(x => Equals(x, CreditWaitResult.Acquired));
        await Assert.That(gate.CurrentCount).IsEqualTo(2);
    }

    [Test]
    public async Task Release_HandsCreditsToWaitersInQueueOrder()
    {
        var gate = new CreditGate();
        var first = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();
        var second = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();

        gate.Release();
        await Assert.That(await first.Timeout(1)).IsEqualTo(CreditWaitResult.Acquired);
        await Task.Delay(50);
        await Assert.That(second.IsCompleted).IsFalse();

        gate.Release();
        await Assert.That(await second.Timeout(1)).IsEqualTo(CreditWaitResult.Acquired);
    }

    [Test]
    public async Task WaitAsync_TimesOut()
    {
        var gate = new CreditGate();
        var result = await gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).AsTask().Timeout(1);
        await Assert.That(result).IsEqualTo(CreditWaitResult.TimedOut);

        // The timed-out waiter no longer takes credit.
        gate.Release();
        await Assert.That(gate.CurrentCount).IsEqualTo(1);
    }

    [Test]
    public async Task WaitAsync_ZeroTimeout_DoesNotPark()
    {
        var gate = new CreditGate();
        var wait = gate.WaitAsync(TimeSpan.Zero, CancellationToken.None);
        await Assert.That(wait.IsCompleted).IsTrue();
        await Assert.That(wait.Result).IsEqualTo(CreditWaitResult.TimedOut);
    }

    [Test]
    public async Task WaitAsync_Canceled_ThrowsAndLeavesTheQueue()
    {
        var gate = new CreditGate();
        using var cts = new CancellationTokenSource();
        var canceled = gate.WaitAsync(Infinite, cts.Token).AsTask();
        var next = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();

        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await canceled.Timeout(1));

        // The credit goes to the next waiter, not the canceled one.
        gate.Release();
        await Assert.That(await next.Timeout(1)).IsEqualTo(CreditWaitResult.Acquired);
        await Assert.That(gate.CurrentCount).IsZero();
    }

    [Test]
    public async Task WaitAsync_AlreadyCanceledToken_Throws()
    {
        var gate = new CreditGate();
        var wait = gate.WaitAsync(Infinite, new CancellationToken(true));
        await Assert.That(wait.IsCanceled).IsTrue();
    }

    [Test]
    public async Task Close_EndsParkedAndLaterWaits()
    {
        var gate = new CreditGate();
        var parked = gate.WaitAsync(Infinite, CancellationToken.None).AsTask();

        gate.Close();
        await Assert.That(await parked.Timeout(1)).IsEqualTo(CreditWaitResult.Closed);

        var later = gate.WaitAsync(Infinite, CancellationToken.None);
        await Assert.That(later.IsCompleted).IsTrue();
        await Assert.That(later.Result).IsEqualTo(CreditWaitResult.Closed);
    }

    [Test]
    public async Task Waiters_AreReusedAcrossManyWaits()
    {
        var gate = new CreditGate();
        for (var i = 0; i < 10_000; i++)
        {
            var wait = gate.WaitAsync(Infinite, CancellationToken.None);
            gate.Release();
            await Assert.That(await wait).IsEqualTo(CreditWaitResult.Acquired);
        }

        await Assert.That(gate.CurrentCount).IsZero();
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
        await Assert.That(acquired).IsEqualTo(Workers * PerWorker);
        await Assert.That(gate.CurrentCount).IsZero();
    }
}