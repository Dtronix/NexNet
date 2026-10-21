using NexNet.Diagnostics;
using NexNet.Messages;
using NUnit.Framework;

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
    public void Table_ReusesFreedSlots()
    {
        var table = new MetricsSessionTable();
        var tags = new MetricsTagSet(null!, true, "tcp");
        var sessions = Enumerable.Range(0, 10).Select(_ => new FakeSession(tags)).ToArray();
        foreach (var session in sessions)
            table.Add(session);

        Assert.That(table.Remove(sessions[3]), Is.True);
        Assert.That(table.Remove(sessions[3]), Is.False);
        Assert.That(sessions[3].MetricsSlot, Is.EqualTo(-1));

        var added = new FakeSession(tags);
        table.Add(added);
        Assert.That(added.MetricsSlot, Is.EqualTo(3));
        Assert.That(table.Count, Is.EqualTo(10));
        Assert.That(table.Capacity, Is.EqualTo(16));
    }

    [Test]
    public void Table_GrowsAndShrinksBack()
    {
        var table = new MetricsSessionTable();
        var tags = new MetricsTagSet(null!, true, "tcp");
        var sessions = Enumerable.Range(0, 10_000).Select(_ => new FakeSession(tags)).ToArray();
        foreach (var session in sessions)
            table.Add(session);
        Assert.That(table.Capacity, Is.GreaterThanOrEqualTo(10_000));

        // Remove in a scattered order; every remaining session keeps a valid slot through compaction.
        var random = new Random(1);
        foreach (var session in sessions.OrderBy(_ => random.Next()).Take(9_990))
            Assert.That(table.Remove(session), Is.True);

        var remaining = sessions.Where(s => s.MetricsSlot >= 0).ToArray();
        Assert.That(remaining, Has.Length.EqualTo(10));
        Assert.That(table.Capacity, Is.LessThanOrEqualTo(64));
        foreach (var session in remaining)
            Assert.That(table.Slots[session.MetricsSlot], Is.SameAs(session));

        foreach (var session in remaining)
            Assert.That(table.Remove(session), Is.True);
        Assert.That(table.Count, Is.EqualTo(0));
        Assert.That(table.Capacity, Is.EqualTo(16));
    }

    [Test]
    public void Collection_RetiresDisconnectedSessionsAndKeepsTheirTotals()
    {
        using var recorder = new MetricsRecorder();
        var metrics = recorder.Metrics;
        var tags = metrics.GetTagSet(false, "tcp");

        var leaked = new FakeSession(tags) { BytesSent = 100 };
        var live = new FakeSession(tags) { BytesSent = 50 };
        metrics.Register(leaked);
        metrics.Register(live);

        Assert.That(recorder.ObserveSum("nexnet.session.active"), Is.EqualTo(2));

        // Disconnected without being retired: the next collection retires it.
        leaked.IsDisconnected = true;
        leaked.DisconnectReason = DisconnectReason.SocketError;
        Assert.That(recorder.ObserveSum("nexnet.session.active"), Is.EqualTo(1));
        Assert.That(metrics.TableSize.Count, Is.EqualTo(1));
        Assert.That(tags.Retired.BytesSent, Is.EqualTo(100));

        var disconnect = recorder.Events("nexnet.session.disconnects").Single();
        Assert.That(disconnect.Has("nexnet.disconnect.reason", "socket_error"), Is.True);

        // Retiring again does nothing.
        metrics.Retire(leaked, DisconnectReason.SocketError, true);
        Assert.That(recorder.Events("nexnet.session.disconnects"), Has.Count.EqualTo(1));
        Assert.That(tags.Retired.BytesSent, Is.EqualTo(100));
    }
}
