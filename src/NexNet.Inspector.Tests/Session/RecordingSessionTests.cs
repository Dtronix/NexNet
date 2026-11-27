using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Session;

/// <summary>Every fixture opens end to end as the viewer opens it: index, sessions, framing, warnings and schema state.</summary>
public class RecordingSessionTests
{
    public static IEnumerable<FixtureKind> Kinds() => Sample.Fixtures.All;

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task Every_session_of_every_fixture_opens_and_indexes(FixtureKind kind)
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(kind));
        var analysis = RecordingAnalysis.Read(File.ReadAllBytes(SessionFixtures.Path(kind)));
        await Assert.That(recording.Sessions.Count).IsEqualTo(analysis.Sessions.Count);

        var truncated = kind == FixtureKind.Truncated;
        await Assert.That(recording.Warnings).IsEqualTo(truncated
            ? RecordingWarnings.IndexFromScan | RecordingWarnings.Damaged
            : RecordingWarnings.None);

        for (var i = 0; i < recording.Sessions.Count; i++)
        {
            var open = recording.Sessions[i];
            var summary = recording.Summarize(open);
            var session = recording.CreateSession(open.SessionId);
            var reports = new List<IndexingProgress>();
            await recording.IndexAsync(session, reports.Add);

            await Assert.That(reports[^1].Blocks).IsEqualTo(reports[^1].TotalBlocks);
            await Assert.That(reports[^1].Fraction).IsEqualTo(1.0);
            await Assert.That(session.OpCount).IsEqualTo(summary.Ops);
            await Assert.That(session.Gaps.Length).IsEqualTo(summary.Gaps);
            await Assert.That(session.FrameCount).IsGreaterThan(0);
            await Assert.That(session.EndTicks).IsGreaterThanOrEqualTo(session.StartTicks);
            await Assert.That(session.Schema.Source).IsEqualTo(SchemaSource.Embedded);
            await Assert.That(session.Schema.Status).IsEqualTo(SchemaStatus.Embedded);
            foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
                await Assert.That(session.GetParser(direction)?.IsFaulted ?? false).IsFalse().Because($"{kind} session {i} {direction}");

            // Without gaps the viewer frames exactly what the Phase 1 replay frames.
            var expected = analysis.Sessions[i];
            if (session.Gaps.Length == 0)
            {
                await Assert.That(session.Assembler.GetFrames(TapDirection.Inbound).Length)
                    .IsEqualTo(expected.Assembler.GetFrames(TapDirection.Inbound).Length);
                await Assert.That(session.Assembler.GetFrames(TapDirection.Outbound).Length)
                    .IsEqualTo(expected.Assembler.GetFrames(TapDirection.Outbound).Length);
                await Assert.That(session.Assembler.Invocations.Length).IsEqualTo(expected.Assembler.Invocations.Length);
            }

            var lastOfTruncated = truncated && i == recording.Sessions.Count - 1;
            await Assert.That(session.State).IsEqualTo(lastOfTruncated
                ? CaptureState.Truncated
                : session.Close!.Value.Kind == NntlCloseKind.Disconnected ? CaptureState.Ended : CaptureState.Stopped);
        }
    }

    [Test]
    public async Task Frames_after_a_drop_gap_are_framed_and_decoded()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = recording.CreateSession(recording.Sessions[0].SessionId);
        recording.Index(session);

        await Assert.That(session.Gaps.Length).IsGreaterThan(0);
        await Assert.That(session.Assembler.GapCount).IsEqualTo(session.Gaps.Length);
        var lastGap = session.Gaps[^1];
        var parser = session.GetParser(lastGap.Direction)!;
        await Assert.That(parser.IsFaulted).IsFalse();
        await Assert.That(parser.GapBytes).IsGreaterThan(0);

        // The store's offsets skip exactly the bytes the gaps lost.
        var store = session.GetStore(lastGap.Direction);
        var lost = session.Gaps.ToArray().Where(g => g.Direction == lastGap.Direction).Sum(g => g.DroppedBytes);
        await Assert.That(store.EndOffset - store.ByteCount).IsEqualTo(lost);

        // Whole frames follow the gap, and the session still ends with its disconnect.
        var frames = session.Assembler.GetFrames(lastGap.Direction).ToArray();
        var after = frames.Where(f => f.StartTicks > lastGap.ToTicks && (f.Flags & FrameFlags.Incomplete) == 0).ToArray();
        await Assert.That(after.Length).IsGreaterThan(0);
        await Assert.That(after.All(f => f.Kind == FrameKind.Frame)).IsTrue();
        await Assert.That(session.Close!.Value.Kind).IsEqualTo(NntlCloseKind.Disconnected);
        var inbound = session.Assembler.GetFrames(TapDirection.Inbound).ToArray();
        await Assert.That(inbound.Any(f => f.Type == MessageType.InvocationCancellation)).IsTrue();
    }

    [Test]
    public async Task Truncated_recording_opens_with_warnings_and_its_last_session_truncated()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.Truncated));
        await Assert.That(recording.IndexSource).IsEqualTo(NntlIndexSource.Scan);
        await Assert.That(recording.Damage.Select(d => d.Kind).ToArray()).IsEquivalentTo(new[] { NntlDamageKind.TornTail });
        var last = recording.CreateSession(recording.Sessions[^1].SessionId);
        recording.Index(last);
        await Assert.That(last.Close).IsNull();
        await Assert.That(last.State).IsEqualTo(CaptureState.Truncated);
        await Assert.That(last.FrameCount).IsGreaterThan(0);
    }

    [Test]
    public async Task Legacy_recording_reports_a_mismatch_with_the_workspace_schema()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.LegacySchema));
        var workspace = NexusSchema.Of<ChatServerNexus>();
        var open = recording.Sessions[0];

        var embedded = recording.CreateSession(open.SessionId, workspace);
        await Assert.That(embedded.Schema.Status).IsEqualTo(SchemaStatus.Mismatch);
        await Assert.That(embedded.Schema.Source).IsEqualTo(SchemaSource.Embedded);
        await Assert.That(embedded.Schema.Schema!.Hash).IsNotEqualTo(workspace.Hash);

        var overridden = recording.CreateSession(open.SessionId, workspace, SchemaChoice.Workspace);
        await Assert.That(overridden.Schema.Source).IsEqualTo(SchemaSource.Workspace);
        await Assert.That(overridden.Schema.Schema).IsSameReferenceAs(workspace);

        var raw = recording.CreateSession(open.SessionId, workspace, SchemaChoice.Raw);
        await Assert.That(raw.Schema.Source).IsEqualTo(SchemaSource.None);
        recording.Index(raw);
        var invocation = raw.Assembler.Invocations.ToArray().First(i => i.HasRequest);
        await Assert.That(raw.Decoder.DecodeArguments(invocation).Problem).IsEqualTo(DecodeProblem.NoSchema);

        // The current Chat recording matches the same workspace schema.
        using var current = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var matching = current.CreateSession(current.Sessions[0].SessionId, workspace);
        await Assert.That(matching.Schema.Status).IsEqualTo(SchemaStatus.Embedded);
    }

    [Test]
    public async Task A_session_without_an_embedded_schema_decodes_raw_or_with_the_workspace_schema()
    {
        var missing = SchemaResolution.Resolve(namesSchema: false, embedded: null, workspace: null, SchemaChoice.Embedded);
        await Assert.That(missing.Status).IsEqualTo(SchemaStatus.Missing);
        await Assert.That(missing.Source).IsEqualTo(SchemaSource.None);

        var workspace = NexusSchema.Of<ChatServerNexus>();
        var fallback = SchemaResolution.Resolve(namesSchema: true, embedded: null, workspace, SchemaChoice.Embedded);
        await Assert.That(fallback.Status).IsEqualTo(SchemaStatus.Unreadable);
        await Assert.That(fallback.Source).IsEqualTo(SchemaSource.Workspace);
    }

    [Test]
    public async Task A_recording_replayed_as_a_live_stream_matches_the_indexed_file()
    {
        var path = SessionFixtures.Path(FixtureKind.ChatServer);
        using var recording = RecordingFile.Open(path);
        var indexed = recording.CreateSession(recording.Sessions[0].SessionId);
        recording.Index(indexed);

        // The live path sees the same blocks in file order, as an endpoint streams them.
        using var reader = NntlReader.Open(File.OpenRead(path));
        var live = new LiveCapture();
        var events = new List<LiveCaptureEvent>();
        foreach (var entry in reader.Index.ToFileOrder())
            events.Add(live.Accept(reader.ReadBlock(entry.Offset)));

        await Assert.That(events[0]).IsEqualTo(LiveCaptureEvent.Prepared);
        await Assert.That(events).Contains(LiveCaptureEvent.SessionOpened);
        await Assert.That(events).Contains(LiveCaptureEvent.SessionClosed);
        var session = live.Session!;
        await Assert.That(session.Source).IsEqualTo(CaptureSource.Live);
        await Assert.That(session.State).IsEqualTo(CaptureState.Ended);
        await Assert.That(session.FrameCount).IsEqualTo(indexed.FrameCount);
        await Assert.That(session.FrameOrder.ToArray()).IsEquivalentTo(indexed.FrameOrder.ToArray());
        await Assert.That(session.Assembler.Invocations.Length).IsEqualTo(indexed.Assembler.Invocations.Length);
        await Assert.That(session.Schema.Status).IsEqualTo(SchemaStatus.Embedded);
    }
}
