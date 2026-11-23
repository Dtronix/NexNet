using System.IO.Pipelines;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;

namespace NexNet.Inspector.Tests.Session;

/// <summary>
/// Save .nntl (I29): a session written by <see cref="CaptureExport"/> reopens as a recording that frames, assembles and
/// decodes exactly as the session it was saved from, whether that came from a file or a live attach stream.
/// </summary>
public class CaptureExportTests
{
    public static IEnumerable<FixtureKind> Kinds() => Sample.Fixtures.All;

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task Every_recorded_session_saves_and_reopens_the_same(FixtureKind kind)
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(kind));
        foreach (var open in recording.Sessions)
        {
            var session = recording.CreateSession(open.SessionId);
            recording.Index(session);

            using var saved = new MemoryStream();
            var result = CaptureExport.Write(session, saved);
            await Assert.That(result.Bytes).IsEqualTo(saved.Length);
            await Assert.That(result.Closed).IsEqualTo(session.Close is not null);

            using var reopened = Reopen(saved, out var copy);
            await AssertSame(session, copy, $"{kind} session {open.SessionId}");
            await Assert.That(reopened.Warnings).IsEqualTo(RecordingWarnings.None);
            await Assert.That(reopened.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        }
    }

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task A_live_attach_saves_in_stream_order_and_reopens_the_same(FixtureKind kind)
    {
        var live = await FeedLiveAsync(SessionFixtures.Path(kind));
        var session = live.Session!;

        using var saved = new MemoryStream();
        CaptureExport.Write(session, saved);
        using var reopened = Reopen(saved, out var copy);

        await AssertSame(session, copy, kind.ToString());
        await Assert.That(copy.Source).IsEqualTo(CaptureSource.File);
        await Assert.That(copy.Header).IsEqualTo(session.Header);
        await Assert.That(copy.SchemaBlock is not null).IsEqualTo(session.SchemaBlock is not null);
    }

    [Test]
    public async Task Redaction_ranges_and_the_snapshot_survive_a_save()
    {
        var live = await FeedLiveAsync(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = live.Session!;
        using var saved = new MemoryStream();
        CaptureExport.Write(session, saved);
        using var reopened = Reopen(saved, out var copy);

        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var expected = session.GetStore(direction).Redactions.ToArray();
            await Assert.That(copy.GetStore(direction).Redactions.ToArray().SequenceEqual(expected)).IsTrue().Because(direction.ToString());
        }

        await Assert.That(copy.SnapshotBlock is not null).IsEqualTo(session.SnapshotBlock is not null);
        if (session.SnapshotBlock is { } snapshot)
        {
            var copied = copy.SnapshotBlock!.Value;
            await Assert.That(copied.Ticks).IsEqualTo(snapshot.Ticks);
            await Assert.That(copied.Sections.Length).IsEqualTo(snapshot.Sections.Length);
        }
    }

    [Test]
    public async Task A_session_still_open_is_saved_as_recording_stopped()
    {
        var path = SessionFixtures.Path(FixtureKind.ChatServer);
        using var recording = RecordingFile.Open(path);
        var session = recording.CreateSession(recording.Sessions[0].SessionId);

        // Half the ops, as a live attach that is still streaming holds them.
        var ops = recording.Reader.Index.GetEntries(NntlBlockKind.TransportOp, session.SessionId).ToArray();
        lock (session.Gate)
        {
            for (var i = 0; i < ops.Length / 2; i++)
            {
                if (recording.Reader.TryReadTransportOp(ops[i].Offset, out var op))
                    session.AddOp(op, ops[i].Offset);
            }
        }

        await Assert.That(session.State).IsEqualTo(CaptureState.Open);
        using var saved = new MemoryStream();
        var result = CaptureExport.Write(session, saved);
        await Assert.That(result.Closed).IsTrue();
        await Assert.That(result.Ops).IsEqualTo(session.OpCount);

        using var reopened = Reopen(saved, out var copy);
        await Assert.That(copy.State).IsEqualTo(CaptureState.Stopped);
        await Assert.That(copy.Close!.Value.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
        await Assert.That(copy.FrameCount).IsGreaterThan(0);
        await Assert.That(copy.OpCount).IsEqualTo(session.OpCount);
    }

    [Test]
    public async Task A_trimmed_live_store_saves_its_lost_prefix_as_a_gap()
    {
        // A retention bound far below the fixture's size: the oldest ops are dropped while it streams.
        var live = await FeedLiveAsync(SessionFixtures.Path(FixtureKind.ChatServer), maxRetainedBytes: 4096);
        var session = live.Session!;
        var inbound = (LiveOpStore)session.Inbound;
        await Assert.That(inbound.TrimmedBytes).IsGreaterThan(0);

        using var saved = new MemoryStream();
        CaptureExport.Write(session, saved);
        using var reopened = Reopen(saved, out var copy);

        // The held ops keep their stream offsets: the gap stands for everything before them.
        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var held = session.GetStore(direction).Ops.ToArray();
            var copied = copy.GetStore(direction).Ops.ToArray();
            await Assert.That(copied.Select(o => (o.Offset, o.Length, o.OpId)).SequenceEqual(held.Select(o => (o.Offset, o.Length, o.OpId))))
                .IsTrue().Because(direction.ToString());
            await Assert.That(copy.GetStore(direction).EndOffset).IsEqualTo(session.GetStore(direction).EndOffset);
        }

        await Assert.That(copy.Gaps.Length).IsGreaterThan(0);
        await Assert.That(copy.State).IsEqualTo(session.State);
    }

    // Streams a fixture through a LiveCapture as an attach stream delivers it (block by block, in file order).
    internal static async Task<LiveCapture> FeedLiveAsync(string path, long maxRetainedBytes = LiveOpStore.DefaultMaxRetainedBytes)
    {
        var live = new LiveCapture(maxRetainedBytes: maxRetainedBytes);
        await using var file = File.OpenRead(path);
        var pipe = PipeReader.Create(file);
        using var reader = new NntlPipeReader(pipe);
        try
        {
            while (await reader.ReadAsync())
                live.Accept(reader.Current);
        }
        catch (NntlFormatException)
        {
            // A torn tail: the endpoint went away mid-block, as the live document's pump sees it.
        }

        await pipe.CompleteAsync();
        if (!live.IsEnded)
            live.Abandon();
        return live;
    }

    private static RecordingFile Reopen(MemoryStream saved, out CaptureSession session)
    {
        var recording = RecordingFile.Open(new MemoryStream(saved.ToArray()));
        if (recording.Sessions.Count != 1)
            throw new InvalidOperationException("A save holds exactly one session, not " + recording.Sessions.Count);
        session = recording.CreateSession(recording.Sessions[0].SessionId);
        recording.Index(session);
        return recording;
    }

    private static async Task AssertSame(CaptureSession expected, CaptureSession actual, string because)
    {
        await Assert.That(actual.SessionId).IsEqualTo(expected.SessionId).Because(because);
        await Assert.That(actual.Role).IsEqualTo(expected.Role).Because(because);
        await Assert.That(actual.StartTicks).IsEqualTo(expected.StartTicks).Because(because);
        await Assert.That(actual.EndTicks).IsEqualTo(expected.EndTicks).Because(because);
        await Assert.That(actual.OpCount).IsEqualTo(expected.OpCount).Because(because);
        await Assert.That(actual.ByteCount).IsEqualTo(expected.ByteCount).Because(because);
        await Assert.That(actual.Gaps.ToArray().SequenceEqual(expected.Gaps.ToArray())).IsTrue().Because(because + ": gaps");
        await Assert.That(actual.State).IsEqualTo(expected.State).Because(because);
        await Assert.That(actual.Close).IsEqualTo(expected.Close).Because(because);
        await Assert.That(actual.Schema.Status).IsEqualTo(expected.Schema.Status).Because(because);
        await Assert.That(actual.Schema.Source).IsEqualTo(expected.Schema.Source).Because(because);
        await Assert.That(actual.Snapshot is not null).IsEqualTo(expected.Snapshot is not null).Because(because);

        // The same frames, completed in the same order, with the same flags; the same entities.
        await Assert.That(actual.FrameOrder.ToArray().SequenceEqual(expected.FrameOrder.ToArray())).IsTrue().Because(because + ": frame order");
        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var want = expected.Assembler.GetFrames(direction).ToArray();
            var got = actual.Assembler.GetFrames(direction).ToArray();
            await Assert.That(got.SequenceEqual(want)).IsTrue().Because($"{because}: {direction} frames");
            var wantBytes = Bytes(expected.GetStore(direction));
            var gotBytes = Bytes(actual.GetStore(direction));
            await Assert.That(gotBytes.SequenceEqual(wantBytes)).IsTrue().Because($"{because}: {direction} bytes");
        }

        await Assert.That(actual.Assembler.Invocations.Length).IsGreaterThan(0).Because(because);
        await Assert.That(actual.Assembler.Invocations.ToArray().SequenceEqual(expected.Assembler.Invocations.ToArray())).IsTrue()
            .Because(because + ": invocations");
        await Assert.That(actual.Assembler.Pipes.ToArray().SequenceEqual(expected.Assembler.Pipes.ToArray())).IsTrue().Because(because + ": pipes");
        await Assert.That(actual.Assembler.Greetings.ToArray().SequenceEqual(expected.Assembler.Greetings.ToArray())).IsTrue()
            .Because(because + ": greetings");
    }

    private static byte[] Bytes(TransportOpStore store)
    {
        var bytes = new List<byte>();
        foreach (var op in store.Ops)
        {
            if (store.TryGetSegment(op.Offset, out var segment))
                bytes.AddRange(segment.Span[..op.Length].ToArray());
        }

        return [.. bytes];
    }
}
