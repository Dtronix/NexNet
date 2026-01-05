using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class SlidingWindowManagerTests
{
    [Test]
    public void DefaultWindowSize_Is64KB()
    {
        Assert.That(SlidingWindowManager.DefaultWindowSize, Is.EqualTo(65536u));
    }

    [Test]
    public void MinWindowSize_Is1KB()
    {
        Assert.That(SlidingWindowManager.MinWindowSize, Is.EqualTo(1024u));
    }

    [Test]
    public void Constructor_SetsWindowSize()
    {
        var manager = new SlidingWindowManager(32768);
        Assert.That(manager.WindowSize, Is.EqualTo(32768u));
    }

    [Test]
    public void Constructor_ThrowsOnTooSmallWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowManager(512));
    }

    [Test]
    public void InitialState_ZeroesAndCanSend()
    {
        var manager = new SlidingWindowManager();

        Assert.That(manager.LastAcked, Is.EqualTo(0u));
        Assert.That(manager.LastSent, Is.EqualTo(0u));
        Assert.That(manager.BytesInFlight, Is.EqualTo(0));
        Assert.That(manager.CanSend, Is.True);
        Assert.That(manager.AvailableWindow, Is.EqualTo(SlidingWindowManager.DefaultWindowSize));
    }

    [Test]
    public void RecordSend_IncrementsSequenceAndBytesInFlight()
    {
        var manager = new SlidingWindowManager(10000);

        var result = manager.RecordSend(1000, out var seq1);
        Assert.That(result, Is.True);
        Assert.That(seq1, Is.EqualTo(1u));
        Assert.That(manager.BytesInFlight, Is.EqualTo(1000));

        result = manager.RecordSend(2000, out var seq2);
        Assert.That(result, Is.True);
        Assert.That(seq2, Is.EqualTo(2u));
        Assert.That(manager.BytesInFlight, Is.EqualTo(3000));
    }

    [Test]
    public void RecordSend_ReducesAvailableWindow()
    {
        var manager = new SlidingWindowManager(10000);

        Assert.That(manager.AvailableWindow, Is.EqualTo(10000));

        manager.RecordSend(3000, out _);
        Assert.That(manager.AvailableWindow, Is.EqualTo(7000));

        manager.RecordSend(5000, out _);
        Assert.That(manager.AvailableWindow, Is.EqualTo(2000));
    }

    [Test]
    public void CanSend_FalseWhenWindowExhausted()
    {
        var manager = new SlidingWindowManager(5000);

        manager.RecordSend(5000, out _);
        Assert.That(manager.CanSend, Is.False);
    }

    [Test]
    public void OnAck_ReducesBytesInFlight()
    {
        var manager = new SlidingWindowManager(10000);

        manager.RecordSend(3000, out _);
        manager.RecordSend(2000, out _);
        Assert.That(manager.BytesInFlight, Is.EqualTo(5000));

        manager.OnAck(1, 10000, 3000);
        Assert.That(manager.BytesInFlight, Is.EqualTo(2000));
        Assert.That(manager.LastAcked, Is.EqualTo(1u));
    }

    [Test]
    public void OnAck_UpdatesWindowSize()
    {
        var manager = new SlidingWindowManager(10000);

        manager.OnAck(0, 20000, 0);
        Assert.That(manager.WindowSize, Is.EqualTo(20000u));
    }

    [Test]
    public void OnAck_EnforcesMinWindowSize()
    {
        var manager = new SlidingWindowManager(10000);

        manager.OnAck(0, 100, 0); // Below minimum
        Assert.That(manager.WindowSize, Is.EqualTo(SlidingWindowManager.MinWindowSize));
    }

    [Test]
    public void Reset_ClearsState()
    {
        var manager = new SlidingWindowManager(10000);

        manager.RecordSend(5000, out _);
        manager.RecordSend(3000, out _);
        manager.OnAck(1, 10000, 5000);

        manager.Reset();

        Assert.That(manager.LastAcked, Is.EqualTo(0u));
        Assert.That(manager.LastSent, Is.EqualTo(0u));
        Assert.That(manager.BytesInFlight, Is.EqualTo(0));
    }

    [Test]
    public async Task WaitForWindowAsync_ReturnsImmediatelyWhenAvailable()
    {
        var manager = new SlidingWindowManager(10000);

        var result = await manager.WaitForWindowAsync(5000);
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task WaitForWindowAsync_WaitsForAck()
    {
        var manager = new SlidingWindowManager(5000);

        // Fill the window
        manager.RecordSend(5000, out _);

        // Start waiting
        var waitTask = manager.WaitForWindowAsync(3000);

        // Should not complete immediately
        await Task.Delay(50);
        Assert.That(waitTask.IsCompleted, Is.False);

        // Acknowledge some data
        manager.OnAck(1, 5000, 3000);

        // Should complete now
        var result = await waitTask;
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task WaitForWindowAsync_ReturnsFalseOnCancellation()
    {
        var manager = new SlidingWindowManager(2000);

        // Fill the window
        manager.RecordSend(2000, out _);

        using var cts = new CancellationTokenSource();
        var waitTask = manager.WaitForWindowAsync(500, cts.Token);

        cts.Cancel();

        var result = await waitTask;
        Assert.That(result, Is.False);
    }

    [Test]
    public void Dispose_ReleasesWaiters()
    {
        var manager = new SlidingWindowManager(2000);
        manager.RecordSend(2000, out _);

        var waitTask = manager.WaitForWindowAsync(500);

        manager.Dispose();

        // Should complete (with false) after dispose
        Assert.That(async () => await waitTask, Throws.Nothing);
    }
}
