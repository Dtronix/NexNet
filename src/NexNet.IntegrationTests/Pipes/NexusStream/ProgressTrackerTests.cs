using System;
using System.Threading;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class ProgressTrackerTests
{
    [Test]
    public void DefaultByteThreshold_Is1MB()
    {
        Assert.That(ProgressTracker.DefaultByteThreshold, Is.EqualTo(1024 * 1024));
    }

    [Test]
    public void DefaultTimeInterval_Is5Seconds()
    {
        Assert.That(ProgressTracker.DefaultTimeInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void Start_BeginsElapsedTracking()
    {
        var tracker = new ProgressTracker();
        Assert.That(tracker.Elapsed, Is.EqualTo(TimeSpan.Zero));

        tracker.Start();
        Thread.Sleep(50);

        Assert.That(tracker.Elapsed, Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    public void Stop_StopsElapsedTracking()
    {
        var tracker = new ProgressTracker();
        tracker.Start();
        Thread.Sleep(50);
        tracker.Stop();

        var elapsedAfterStop = tracker.Elapsed;
        Thread.Sleep(50);

        Assert.That(tracker.Elapsed, Is.EqualTo(elapsedAfterStop));
    }

    [Test]
    public void ShouldReport_ReturnsTrueOnStateChange()
    {
        var tracker = new ProgressTracker(byteThreshold: 1000000);
        tracker.Start();

        // First call with Active - sets baseline
        tracker.ShouldReport(0, 0, TransferState.Active);

        // Small byte transfer - should not report
        Assert.That(tracker.ShouldReport(100, 0, TransferState.Active), Is.False);

        // State change to Complete - should report
        Assert.That(tracker.ShouldReport(100, 0, TransferState.Complete), Is.True);
    }

    [Test]
    public void ShouldReport_ReturnsTrueOnByteThreshold()
    {
        var tracker = new ProgressTracker(byteThreshold: 1000);
        tracker.Start();

        // Set baseline
        tracker.ShouldReport(0, 0, TransferState.Active);

        // Below threshold
        Assert.That(tracker.ShouldReport(500, 0, TransferState.Active), Is.False);

        // At threshold
        Assert.That(tracker.ShouldReport(1000, 0, TransferState.Active), Is.True);
    }

    [Test]
    public void ShouldReport_ReturnsTrueOnTimeInterval()
    {
        var tracker = new ProgressTracker(byteThreshold: 1000000, timeInterval: TimeSpan.FromMilliseconds(50));
        tracker.Start();

        // Set baseline
        tracker.ShouldReport(0, 0, TransferState.Active);

        // Immediately after - should not report
        Assert.That(tracker.ShouldReport(10, 0, TransferState.Active), Is.False);

        // Wait for time interval
        Thread.Sleep(60);

        // Should report due to time
        Assert.That(tracker.ShouldReport(20, 0, TransferState.Active), Is.True);
    }

    [Test]
    public void ShouldReport_CombinesReadAndWriteBytes()
    {
        var tracker = new ProgressTracker(byteThreshold: 1000);
        tracker.Start();

        // Set baseline
        tracker.ShouldReport(0, 0, TransferState.Active);

        // Read 400 + Write 400 = 800, below 1000
        Assert.That(tracker.ShouldReport(400, 400, TransferState.Active), Is.False);

        // Read 400 + Write 600 = 1000, at threshold
        Assert.That(tracker.ShouldReport(400, 600, TransferState.Active), Is.True);
    }

    [Test]
    public void ShouldReport_AlwaysReportsComplete()
    {
        var tracker = new ProgressTracker(byteThreshold: 1000000);
        tracker.Start();

        // Complete should always report
        Assert.That(tracker.ShouldReport(0, 0, TransferState.Complete), Is.True);
    }

    [Test]
    public void ShouldReport_AlwaysReportsFailed()
    {
        var tracker = new ProgressTracker(byteThreshold: 1000000);
        tracker.Start();

        // Failed should always report
        Assert.That(tracker.ShouldReport(0, 0, TransferState.Failed), Is.True);
    }

    [Test]
    public void CalculateRate_ReturnsZeroWhenNoTime()
    {
        var tracker = new ProgressTracker();

        // Not started - should return 0
        Assert.That(tracker.CalculateRate(1000), Is.EqualTo(0));
    }

    [Test]
    public void CalculateRate_CalculatesCorrectly()
    {
        var tracker = new ProgressTracker();
        tracker.Start();

        // Wait a bit and check rate
        Thread.Sleep(100);
        var rate = tracker.CalculateRate(1000);

        // Rate should be roughly 1000 / 0.1 = 10000 bytes/sec
        // But allow for timing variance
        Assert.That(rate, Is.GreaterThan(5000));
        Assert.That(rate, Is.LessThan(20000));
    }

    [Test]
    public void ForceNextReport_CausesNextReportToReturnTrue()
    {
        var tracker = new ProgressTracker(byteThreshold: 1000000);
        tracker.Start();

        // Set baseline
        tracker.ShouldReport(0, 0, TransferState.Active);

        // Small increment - should not report normally
        Assert.That(tracker.ShouldReport(10, 0, TransferState.Active), Is.False);

        // Force next report
        tracker.ForceNextReport();

        // Should now report
        Assert.That(tracker.ShouldReport(20, 0, TransferState.Active), Is.True);
    }
}
