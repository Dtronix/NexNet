using System.Buffers;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Tap;

/// <summary>Drop and Block overflow with a viewer that stops reading.</summary>
public class TapOverflowTests
{
    private const int Payload = 16 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Drop_reports_gaps_with_exact_counts_and_never_slows_the_session()
    {
        WatchedTap? watched = null;
        await using var h = new TapHarness(TapTransport.Uds, new InspectorOptions { RingCapacity = 8 * 1024 },
            wrapTap: inspector => watched = new WatchedTap(inspector));
        await h.StartAsync();
        await h.StartEndpointAsync();
        var client = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync();
        var tapped = h.Inspector.FindSession(session.Id)!;
        var before = await QuietCounters(h, tapped, client);
        long[] callsBefore = [watched!.Calls(TapDirection.Inbound), watched.Calls(TapDirection.Outbound)];

        await using var stream = await viewer.AttachAsync(new InspectorAttachRequest(InspectorTargetKind.Session, session.Id,
            InspectorOverflow.Drop, Snapshot: false, Record: false));

        // The viewer reads nothing while the session moves megabytes: the session must not wait for it. That is
        // proven by what the tap handed back, not by how long the burst took: under load a burst is slow either way,
        // while a Drop sink that held the session would return a pending task. The timeout only stops a hang.
        var data = new byte[Payload];
        for (var i = 0; i < 300; i++)
            await client.Proxy.Echo(data).AsTask().WaitAsync(Wait);
        var after = await QuietCounters(h, tapped, client);
        await Assert.That(watched.Held).IsEqualTo(0);

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
            await Assert.That(watched.Calls(direction) - callsBefore[d]).IsEqualTo(after.Ops[d] - before.Ops[d]);
            await Assert.That(ops.All(o => o.OpId >= before.Ops[d] && o.OpId < after.Ops[d])).IsTrue();
            await Assert.That(ops.Select(o => o.OpId).Distinct().Count()).IsEqualTo(ops.Length);
            await Assert.That(ops.Length + gaps.Sum(g => g.DroppedOps)).IsEqualTo(after.Ops[d] - before.Ops[d]);
            await Assert.That(ops.Sum(o => (long)o.Bytes.Length) + gaps.Sum(g => g.DroppedBytes))
                .IsEqualTo(after.Bytes[d] - before.Bytes[d]);
            await Assert.That(gaps.All(g => g.FromTicks <= g.ToTicks)).IsTrue();
        }
    }

    /// <summary>
    /// Regression for I09b. Many inspectors at once, each with a tiny ring and a tiny stream window, so every attach
    /// overflows and every flush waits on its viewer. Half the viewers are stalled when they detach (the sender is
    /// waiting for window credit at that moment), half are already reading. Every stream must stay checksum-clean
    /// and end with its footer, and per direction recorded ops plus dropped ops must equal what the session moved.
    /// </summary>
    [Test]
    public async Task Concurrent_viewers_on_tiny_windows_lose_no_op_and_keep_the_stream_intact()
    {
        const int inspectors = 8;
        var runs = new Task[inspectors];
        for (var i = 0; i < inspectors; i++)
        {
            var stalledAtDetach = i % 2 == 0;
            runs[i] = Task.Run(() => DetachUnderPressure(stalledAtDetach));
        }

        await Task.WhenAll(runs).WaitAsync(Wait);
    }

    private static async Task DetachUnderPressure(bool stalledAtDetach)
    {
        const int window = 4 * 1024;
        await using var h = new TapHarness(TapTransport.Uds, new InspectorOptions { RingCapacity = 8 * 1024 });
        await h.StartAsync();
        await h.StartEndpointAsync(pipeWindow: window);
        var client = await h.ConnectClientAsync();
        var session = await h.WaitForSessionAsync();
        var viewer = await h.ConnectViewerAsync(pipeWindow: window);
        var tapped = h.Inspector.FindSession(session.Id)!;
        var before = await QuietCounters(h, tapped, client);

        await using var stream = await viewer.AttachAsync(new InspectorAttachRequest(InspectorTargetKind.Session, session.Id,
            InspectorOverflow.Drop, Snapshot: false, Record: false));

        // Far more than the ring and the window hold, so the sender ends up waiting for credit with blocks unsent.
        var data = new byte[4 * 1024];
        for (var i = 0; i < 40; i++)
            await client.Proxy.Echo(data).AsTask().WaitAsync(Wait);
        var after = await QuietCounters(h, tapped, client);

        Task detach;
        Task<TapCapture> reading;
        if (stalledAtDetach)
        {
            // The detach reaches the sink while its flush waits on the stalled viewer; the viewer reads only after.
            detach = viewer.DetachAsync();
            await Task.Delay(50);
            reading = TapCapture.ReadAllAsync(stream);
        }
        else
        {
            reading = TapCapture.ReadAllAsync(stream);
            detach = viewer.DetachAsync();
        }

        var capture = await reading.WaitAsync(Wait);
        await detach.WaitAsync(Wait);
        await Assert.That(capture.Complete).IsTrue();

        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var d = (int)direction;
            var ops = capture.Ops(session.Id, direction).ToArray();
            var gaps = capture.Gaps(session.Id, direction).ToArray();
            await Assert.That(ops.Select(o => o.OpId).Distinct().Count()).IsEqualTo(ops.Length);
            await Assert.That(ops.Length + gaps.Sum(g => g.DroppedOps)).IsEqualTo(after.Ops[d] - before.Ops[d]);
            await Assert.That(ops.Sum(o => (long)o.Bytes.Length) + gaps.Sum(g => g.DroppedBytes))
                .IsEqualTo(after.Bytes[d] - before.Bytes[d]);
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
        var before = await QuietCounters(h, tapped, client);

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
        var after = await QuietCounters(h, tapped, client);
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

    /// <summary>
    /// The session's op and byte counters once the server has caught up with the client. The ops and the sent bytes
    /// are counted before the bytes reach the peer, but the server counts what it read only after processing it, which
    /// can be after its reply has already reached the client; the client counts what it sent before flushing it.
    /// </summary>
    private static async Task<(long[] Ops, long[] Bytes)> QuietCounters(TapHarness h, TappedSession tapped,
        NexusClient<TapClientNexus, TapClientNexus.ServerProxy> client)
    {
        var sent = client.GetSnapshot()!.BytesSent;
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var snapshot = h.Server.GetSnapshot().Sessions.Single(s => s.Id == tapped.Id);
            if (snapshot.BytesReceived == sent)
            {
                return ([tapped.NextOpId(TapDirection.Inbound), tapped.NextOpId(TapDirection.Outbound)],
                    [snapshot.BytesReceived, snapshot.BytesSent]);
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The server counted {snapshot.BytesReceived} bytes read; the client sent {sent}.");
            await Task.Delay(5);
        }
    }

    /// <summary>Sits between the session and the inspector, counting the ops it passes and the pending tasks it gets back.</summary>
    private sealed class WatchedTap(INexusTap inner) : INexusTap
    {
        private readonly long[] _calls = new long[2];
        private long _held;

        public long Calls(TapDirection direction) => Interlocked.Read(ref _calls[(int)direction]);

        /// <summary>Reads and writes the inspector held, i.e. returned a task that was not complete.</summary>
        public long Held => Interlocked.Read(ref _held);

        public INexusSessionTap? SessionStarted(in NexusTapSessionInfo session) =>
            inner.SessionStarted(session) is { } tap ? new Session(this, tap) : null;

        private ValueTask Watch(TapDirection direction, ValueTask pending)
        {
            Interlocked.Increment(ref _calls[(int)direction]);
            if (!pending.IsCompleted)
                Interlocked.Increment(ref _held);
            return pending;
        }

        private sealed class Session(WatchedTap owner, INexusSessionTap inner) : INexusSessionTap
        {
            public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp) =>
                owner.Watch(TapDirection.Inbound, inner.OnRead(data, timestamp));

            public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp) =>
                owner.Watch(TapDirection.Outbound, inner.OnWrite(data, timestamp));

            public void OnConnected(long sessionId) => inner.OnConnected(sessionId);

            public void OnClosed(DisconnectReason reason) => inner.OnClosed(reason);
        }
    }
}
