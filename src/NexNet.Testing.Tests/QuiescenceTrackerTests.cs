using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Testing.Quiescence;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class QuiescenceTrackerTests
{
    [Test]
    public async Task EmptyTracker_QuiescesImmediately()
    {
        var tracker = new QuiescenceTracker();
        await tracker.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task DispatchEnterExit_QuiescesAfterExit()
    {
        var tracker = new QuiescenceTracker();
        var counters = tracker.GetCountersFor(sessionId: 1);

        counters.EnterDispatch();
        tracker.SignalChange();

        var quiesceTask = tracker.QuiesceAsync();
        // Should not complete while a dispatch is active.
        await Task.Delay(50);
        Assert.That(quiesceTask.IsCompleted, Is.False);

        counters.ExitDispatch();
        tracker.SignalChange();

        await quiesceTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task ActivePipes_BlockQuiescence()
    {
        var tracker = new QuiescenceTracker();
        var counters = tracker.GetCountersFor(sessionId: 1);

        counters.OpenPipe();
        tracker.SignalChange();

        var quiesceTask = tracker.QuiesceAsync();
        await Task.Delay(50);
        Assert.That(quiesceTask.IsCompleted, Is.False);

        counters.ClosePipe();
        tracker.SignalChange();

        await quiesceTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task PendingInvocationProbe_BlocksUntilZero()
    {
        var tracker = new QuiescenceTracker();
        int pending = 2;
        var key = new object();
        tracker.RegisterPendingInvocationProbe(key, () => pending);

        var quiesceTask = tracker.QuiesceAsync();
        await Task.Delay(50);
        Assert.That(quiesceTask.IsCompleted, Is.False);

        pending = 0;
        tracker.SignalChange();
        await quiesceTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task PendingInvocationProbe_Unregister_RemovesProbe()
    {
        var tracker = new QuiescenceTracker();
        int pending = 5;
        var key = new object();
        tracker.RegisterPendingInvocationProbe(key, () => pending);

        var quiesceTask = tracker.QuiesceAsync();
        await Task.Delay(50);
        Assert.That(quiesceTask.IsCompleted, Is.False);

        tracker.UnregisterPendingInvocationProbe(key);
        tracker.SignalChange();
        await quiesceTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task QuiesceAsync_HonorsCancellation()
    {
        var tracker = new QuiescenceTracker();
        var counters = tracker.GetCountersFor(sessionId: 1);
        counters.EnterDispatch();

        using var cts = new CancellationTokenSource();
        var quiesceTask = tracker.QuiesceAsync(cts.Token);
        await Task.Delay(50);
        Assert.That(quiesceTask.IsCompleted, Is.False);

        cts.Cancel();
        Assert.CatchAsync<OperationCanceledException>(
            async () => await quiesceTask.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task BytesInTransit_BlocksQuiescence()
    {
        var tracker = new QuiescenceTracker();
        var counters = tracker.GetCountersFor(sessionId: 1);
        counters.AddBytesInTransit(128);
        tracker.SignalChange();

        var quiesceTask = tracker.QuiesceAsync();
        await Task.Delay(50);
        Assert.That(quiesceTask.IsCompleted, Is.False);

        counters.AddBytesInTransit(-128);
        tracker.SignalChange();
        await quiesceTask.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
