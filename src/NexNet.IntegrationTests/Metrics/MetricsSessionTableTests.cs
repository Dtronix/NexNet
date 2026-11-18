using NexNet.Diagnostics;
using NexNet.Messages;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// The session table behind the observable instruments: slot reuse, shrinking, and retiring sessions a disconnect missed.
/// </summary>
internal class MetricsSessionTableTests
{
    private sealed class FakeSession(MetricsTagSet tags) : IMeteredSession
    {
        public MetricsTagSet MetricsTags { get; } = tags;
        public int MetricsSlot { get; set; } = -1;
        public long MetricsStartTimestamp { get; set; }
        public bool IsDisconnected { get; set; }
        public DisconnectReason DisconnectReason { get; set; }
        public bool IsEstablished { get; set; } = true;
        public long BytesSent { get; set; }

        public void CollectMetrics(ref SessionMetricsTotals totals)
        {
            totals.Sessions++;
            totals.BytesSent += BytesSent;
        }
    }

    [Test]
    public async Task Table_ReusesFreedSlots()
    {
        var table = new MetricsSessionTable();
        var tags = new MetricsTagSet(null!, true, "tcp");
        var sessions = Enumerable.Range(0, 10).Select(_ => new FakeSession(tags)).ToArray();
        foreach (var session in sessions)
            table.Add(session);

        await Assert.That(table.Remove(sessions[3])).IsTrue();
        await Assert.That(table.Remove(sessions[3])).IsFalse();
        await Assert.That(sessions[3].MetricsSlot).IsEqualTo(-1);

        var added = new FakeSession(tags);
        table.Add(added);
        await Assert.That(added.MetricsSlot).IsEqualTo(3);
        await Assert.That(table.Count).IsEqualTo(10);
        await Assert.That(table.Capacity).IsEqualTo(16);
    }

    [Test]
    public async Task Table_GrowsAndShrinksBack()
    {
        var table = new MetricsSessionTable();
        var tags = new MetricsTagSet(null!, true, "tcp");
        var sessions = Enumerable.Range(0, 10_000).Select(_ => new FakeSession(tags)).ToArray();
        foreach (var session in sessions)
            table.Add(session);
        await Assert.That(table.Capacity).IsGreaterThanOrEqualTo(10_000);

        // Remove in a scattered order; every remaining session keeps a valid slot through compaction.
        var random = new Random(1);
        foreach (var session in sessions.OrderBy(_ => random.Next()).Take(9_990))
            await Assert.That(table.Remove(session)).IsTrue();

        var remaining = sessions.Where(s => s.MetricsSlot >= 0).ToArray();
        await Assert.That(remaining).Count().IsEqualTo(10);
        await Assert.That(table.Capacity).IsLessThanOrEqualTo(64);
        foreach (var session in remaining)
            await Assert.That(table.Slots[session.MetricsSlot]).IsSameReferenceAs(session);

        foreach (var session in remaining)
            await Assert.That(table.Remove(session)).IsTrue();
        await Assert.That(table.Count).IsEqualTo(0);
        await Assert.That(table.Capacity).IsEqualTo(16);
    }

    [Test]
    public async Task RetireBetweenCollections_CountsTheSessionOnce()
    {
        using var recorder = new MetricsRecorder();
        var metrics = recorder.Metrics;
        var tags = metrics.GetTagSet(true, "tcp");
        var session = new FakeSession(tags) { BytesSent = 100 };
        metrics.Register(session);

        // Collections share one pass for a while; a retire in between must not count the session twice.
        metrics.RefreshInterval = long.MaxValue / 2;
        await Assert.That(recorder.ObserveSum("nexnet.io.bytes", ("nexnet.direction", "sent"))).IsEqualTo(100);
        metrics.Retire(session, DisconnectReason.Graceful, true);
        await Assert.That(recorder.ObserveSum("nexnet.io.bytes", ("nexnet.direction", "sent"))).IsEqualTo(100);
    }

    [Test]
    public async Task Collection_RetiresDisconnectedSessionsAndKeepsTheirTotals()
    {
        using var recorder = new MetricsRecorder();
        var metrics = recorder.Metrics;
        var tags = metrics.GetTagSet(false, "tcp");

        var leaked = new FakeSession(tags) { BytesSent = 100 };
        var live = new FakeSession(tags) { BytesSent = 50 };
        metrics.Register(leaked);
        metrics.Register(live);

        await Assert.That(recorder.ObserveSum("nexnet.session.active")).IsEqualTo(2);

        // Disconnected without being retired: the next collection retires it.
        leaked.IsDisconnected = true;
        leaked.DisconnectReason = DisconnectReason.SocketError;
        await Assert.That(recorder.ObserveSum("nexnet.session.active")).IsEqualTo(1);
        await Assert.That(metrics.TableSize.Count).IsEqualTo(1);
        await Assert.That(tags.Retired.BytesSent).IsEqualTo(100);

        var disconnect = recorder.Events("nexnet.session.disconnects").Single();
        await Assert.That(disconnect.Has("nexnet.disconnect.reason", "socket_error")).IsTrue();

        // Retiring again does nothing.
        metrics.Retire(leaked, DisconnectReason.SocketError, true);
        await Assert.That(recorder.Events("nexnet.session.disconnects")).Count().IsEqualTo(1);
        await Assert.That(tags.Retired.BytesSent).IsEqualTo(100);
    }
}