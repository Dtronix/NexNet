using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;

namespace NexNet.Inspector.Tests.Tap;

/// <summary>Drop and Block overflow with a viewer that stops reading.</summary>
public class TapOverflowTests
{
    private const int Payload = 16 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Drop_reports_gaps_with_exact_counts_and_never_slows_the_session()
    {
        await using var h = new TapHarness(TapTransport.Uds, new InspectorOptions { RingCapacity = 8 * 1024 });
        await h.StartAsync();
        await h.StartEndpointAsync();
        var client = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync();
        var tapped = h.Inspector.FindSession(session.Id)!;
        var before = Counters(h, tapped);

        await using var stream = await viewer.AttachAsync(new InspectorAttachRequest(InspectorTargetKind.Session, session.Id,
            InspectorOverflow.Drop, Snapshot: false, Record: false));

        // The viewer reads nothing while the session moves megabytes: the session must not wait for it.
        var data = new byte[Payload];
        for (var i = 0; i < 300; i++)
            await client.Proxy.Echo(data).AsTask().WaitAsync(Wait);
        var after = Counters(h, tapped);

        var reading = TapCapture.ReadAllAsync(stream);
        await viewer.DetachAsync().WaitAsync(Wait);
        var capture = await reading.WaitAsync(Wait);
        await Assert.That(capture.Complete).IsTrue();

        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var d = (int)direction;
            var ops = capture.Ops(session.Id, direction).ToArray();
            var gaps = capture.Gaps(session.Id, direction).ToArray();
            await Assert.That(gaps.Length).IsGreaterThan(0);
            await Assert.That(ops.All(o => o.OpId >= before.Ops[d] && o.OpId < after.Ops[d])).IsTrue();
            await Assert.That(ops.Select(o => o.OpId).Distinct().Count()).IsEqualTo(ops.Length);
            await Assert.That(ops.Length + gaps.Sum(g => g.DroppedOps)).IsEqualTo(after.Ops[d] - before.Ops[d]);
            await Assert.That(ops.Sum(o => (long)o.Bytes.Length) + gaps.Sum(g => g.DroppedBytes))
                .IsEqualTo(after.Bytes[d] - before.Bytes[d]);
            await Assert.That(gaps.All(g => g.FromTicks <= g.ToTicks)).IsTrue();
        }
    }

    [Test]
    public async Task Block_is_lossless_and_holds_the_session_back()
    {
        await using var h = new TapHarness(TapTransport.Uds, new InspectorOptions { RingCapacity = 8 * 1024 });
        await h.StartAsync();
        await h.StartEndpointAsync();
        var client = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync();
        var tapped = h.Inspector.FindSession(session.Id)!;
        var before = Counters(h, tapped);

        await using var stream = await viewer.AttachAsync(new InspectorAttachRequest(InspectorTargetKind.Session, session.Id,
            InspectorOverflow.Block, Snapshot: false, Record: false));

        var data = new byte[Payload];
        var burst = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
                await client.Proxy.Echo(data);
        });

        // The viewer reads nothing yet: the session runs at its pace, so the burst cannot finish.
        await Task.Delay(1500);
        await Assert.That(burst.IsCompleted).IsFalse();

        var reading = TapCapture.ReadAllAsync(stream);
        await burst.WaitAsync(Wait);
        var after = Counters(h, tapped);
        await viewer.DetachAsync().WaitAsync(Wait);
        var capture = await reading.WaitAsync(Wait);
        await Assert.That(capture.Complete).IsTrue();
        await Assert.That(capture.Blocks.Any(b => b.Kind == NntlBlockKind.Gap)).IsFalse();

        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var d = (int)direction;
            var ids = capture.Ops(session.Id, direction).Select(o => o.OpId).ToArray();
            var expected = Enumerable.Range(0, (int)(after.Ops[d] - before.Ops[d])).Select(i => before.Ops[d] + i).ToArray();
            await Assert.That(ids).IsEquivalentTo(expected);
            await Assert.That(capture.Ops(session.Id, direction).Sum(o => (long)o.Bytes.Length))
                .IsEqualTo(after.Bytes[d] - before.Bytes[d]);
        }
    }

    private static (long[] Ops, long[] Bytes) Counters(TapHarness h, TappedSession tapped)
    {
        var snapshot = h.Server.GetSnapshot().Sessions.Single(s => s.Id == tapped.Id);
        return ([tapped.NextOpId(TapDirection.Inbound), tapped.NextOpId(TapDirection.Outbound)],
            [snapshot.BytesReceived, snapshot.BytesSent]);
    }
}
