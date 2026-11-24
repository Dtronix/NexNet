using NexNet.Inspector.Cli;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Session;

/// <summary>
/// Export section as .nntl (I40): <see cref="CaptureExport.WriteRange"/> writes the frames that start inside a range,
/// with the handshake and the schema, as a recording that opens on its own, frames and decodes the same frames, and that
/// nexnet-tap info reads cleanly.
/// </summary>
public class CaptureExportRangeTests
{
    [Test]
    public async Task A_middle_section_reopens_with_the_same_frames_the_handshake_and_its_schema()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = Indexed(recording);
        var (from, to) = Invocations(session);

        using var saved = new MemoryStream();
        var result = CaptureExport.WriteRange(session, saved, new CaptureExportRange(from, to));
        await Assert.That(result.Handshake).IsTrue();
        await Assert.That(result.Bytes).IsEqualTo(saved.Length);
        await Assert.That(result.Frames).IsGreaterThan(0);

        using var reopened = Reopen(saved, out var copy);
        await Assert.That(reopened.Warnings).IsEqualTo(RecordingWarnings.None);
        await Assert.That(copy.SessionId).IsEqualTo(session.SessionId);
        await Assert.That(copy.Schema.Source).IsEqualTo(SchemaSource.Embedded);
        await Assert.That(copy.Close!.Value.Kind).IsEqualTo(NntlCloseKind.RecordingStopped);
        await Assert.That(copy.Close!.Value.Ticks).IsLessThanOrEqualTo(to);

        // The preamble and both greetings, so the file frames and decodes from its first byte.
        await Assert.That(copy.Assembler.Greetings.Length).IsEqualTo(session.Assembler.Greetings.Length);
        var copiedFrames = 0;
        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var want = InRange(session, direction, from, to);
            var got = copy.Assembler.GetFrames(direction).ToArray().Where(f => f.StartTicks >= from).Select(f => Describe(copy, f)).ToArray();
            await Assert.That(got.SequenceEqual(want)).IsTrue().Because(direction + ": the section's frames, bytes and all");
            await Assert.That(copy.Assembler.GetFrames(direction).ToArray().Any(f => (f.Flags & FrameFlags.Incomplete) != 0)).IsFalse();
            copiedFrames += got.Length;
        }

        await Assert.That(copiedFrames).IsEqualTo(result.Frames);

        // Invocations whose request lies in the section decode by the embedded schema, as they did in the session.
        await Assert.That(Decoded(copy, from, to)).IsEqualTo(Decoded(session, from, to));
        await Assert.That(Decoded(copy, from, to).Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Nexnet_tap_info_reads_an_exported_section_cleanly()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = Indexed(recording);
        var (from, to) = MiddleThird(session);
        var path = Path.Combine(Path.GetTempPath(), "nexnet-range-" + Guid.NewGuid().ToString("N") + ".nntl");
        try
        {
            using (var file = File.Create(path))
                CaptureExport.WriteRange(session, file, new CaptureExportRange(from, to));

            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await new TapCli(output, error).RunAsync(["info", path], null, CancellationToken.None);
            await Assert.That(code).IsEqualTo(0).Because(error.ToString());
            await Assert.That(error.ToString()).IsEmpty();

            RecordingSummary info;
            using (var stream = File.OpenRead(path))
                info = RecordingAnalyzer.Analyze(stream, "x.nntl", messages: true);
            await Assert.That(info.IndexSource).IsEqualTo(NntlIndexSource.Footer);
            await Assert.That(info.Sessions.Count).IsEqualTo(1);
            await Assert.That(info.Sessions[0].Id).IsEqualTo(session.SessionId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Without_the_handshake_or_schema_the_section_still_frames_from_its_first_frame()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = Indexed(recording);
        var (from, to) = MiddleThird(session);

        using var saved = new MemoryStream();
        var result = CaptureExport.WriteRange(session, saved, new CaptureExportRange(from, to, IncludeHandshake: false, IncludeSchema: false));
        await Assert.That(result.Handshake).IsFalse();

        using var reopened = Reopen(saved, out var copy);
        await Assert.That(copy.SchemaBlock).IsNull();
        await Assert.That(copy.Assembler.Greetings.Length).IsEqualTo(0);
        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var want = InRange(session, direction, from, to);
            var got = copy.Assembler.GetFrames(direction).ToArray().Select(f => Describe(copy, f)).ToArray();
            await Assert.That(got.SequenceEqual(want)).IsTrue().Because(direction.ToString());
        }
    }

    [Test]
    public async Task A_section_inside_an_open_pipe_carries_a_snapshot_that_names_it()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = Indexed(recording);

        // The second data frame of a pipe with several: the pipe opened before the section starts.
        var writes = session.Assembler.GetFrames(TapDirection.Inbound).ToArray().Concat(session.Assembler.GetFrames(TapDirection.Outbound).ToArray())
            .Where(f => f.Type == MessageType.DuplexPipeWrite).GroupBy(f => f.TargetId).First(g => g.Count() >= 3).OrderBy(f => f.StartTicks).ToArray();
        var from = writes[1].StartTicks;
        var to = writes[^1].StartTicks;

        using var saved = new MemoryStream();
        var result = CaptureExport.WriteRange(session, saved, new CaptureExportRange(from, to));
        await Assert.That(result.Snapshot).IsTrue();

        using var reopened = Reopen(saved, out var copy);
        await Assert.That(copy.Snapshot).IsNotNull();
        await Assert.That(copy.Snapshot!.Pipes.Length).IsGreaterThan(0);
        var seeded = copy.Assembler.Pipes.ToArray().Where(p => p.FromSnapshot).ToArray();
        await Assert.That(seeded.Length).IsGreaterThan(0);
        await Assert.That(seeded.Any(p => p.InboundBytes + p.OutboundBytes > 0)).IsTrue().Because("the section's pipe writes land on the seeded pipe");
    }

    [Test]
    public async Task A_live_attach_exports_a_section_that_reopens()
    {
        var live = await CaptureExportTests.FeedLiveAsync(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = live.Session!;
        var (from, to) = MiddleThird(session);

        using var saved = new MemoryStream();
        var result = CaptureExport.WriteRange(session, saved, new CaptureExportRange(from, to));
        using var reopened = Reopen(saved, out var copy);
        await Assert.That(copy.Source).IsEqualTo(CaptureSource.File);
        await Assert.That(result.Frames).IsGreaterThan(0);
        foreach (var direction in new[] { TapDirection.Inbound, TapDirection.Outbound })
        {
            var want = InRange(session, direction, from, to);
            var got = copy.Assembler.GetFrames(direction).ToArray().Where(f => f.StartTicks >= from).Select(f => Describe(copy, f)).ToArray();
            await Assert.That(got.SequenceEqual(want)).IsTrue().Because(direction.ToString());
        }
    }

    [Test]
    public async Task An_empty_range_writes_the_handshake_and_a_close()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatServer));
        var session = Indexed(recording);
        var before = session.StartTicks - 1000;

        using var saved = new MemoryStream();
        var result = CaptureExport.WriteRange(session, saved, new CaptureExportRange(before - 10, before));
        await Assert.That(result.Frames).IsEqualTo(0);
        using var reopened = Reopen(saved, out var copy);
        await Assert.That(copy.Close).IsNotNull();
        await Assert.That(copy.Assembler.Greetings.Length).IsEqualTo(session.Assembler.Greetings.Length);
    }

    // The method names of the invocations whose request starts in the range and decodes by the schema.
    private static string Decoded(CaptureSession session, long from, long to)
    {
        var names = new List<string>();
        foreach (var invocation in session.Assembler.Invocations)
        {
            if (!invocation.HasRequest || !session.TryGetFrame(new FrameKey(invocation.Request.Direction, invocation.Request.Seq), out var frame)
                || frame.StartTicks < from || frame.StartTicks > to)
                continue;
            if (session.Decoder.TryGetMethod(invocation, out var method))
                names.Add(method.Name + ":" + session.Decoder.DecodeArguments(invocation).Fields.Length);
        }

        return string.Join(",", names);
    }

    private static CaptureSession Indexed(RecordingFile recording)
    {
        var session = recording.CreateSession(recording.Sessions[0].SessionId);
        recording.Index(session);
        return session;
    }

    // From the second invocation's request to the last but one's: a section with calls in it, the session's start left out.
    private static (long From, long To) Invocations(CaptureSession session)
    {
        var starts = session.Assembler.Invocations.ToArray().Where(i => i.HasRequest).Select(i =>
        {
            session.TryGetFrame(new FrameKey(i.Request.Direction, i.Request.Seq), out var f);
            return f.StartTicks;
        }).Order().ToArray();
        return (starts[1], starts[^2]);
    }

    // The middle third of the session's frames by time.
    private static (long From, long To) MiddleThird(CaptureSession session)
    {
        var ticks = session.FrameOrder.ToArray().Select(k =>
        {
            session.TryGetFrame(k, out var f);
            return f.StartTicks;
        }).Order().ToArray();
        return (ticks[ticks.Length / 3], ticks[2 * ticks.Length / 3]);
    }

    // A direction's frames that start in the range and lie past the handshake, as type, length and body bytes.
    private static string[] InRange(CaptureSession session, TapDirection direction, long from, long to) =>
        session.Assembler.GetFrames(direction).ToArray().Where(f => f.StartTicks >= from && f.StartTicks <= to
            && f.Type is not (MessageType.ClientGreeting or MessageType.ServerGreeting or MessageType.ClientGreetingReconnection)
            && f.Kind != FrameKind.Preamble).Select(f => Describe(session, f)).ToArray();

    private static string Describe(CaptureSession session, FrameRecord frame)
    {
        var bytes = new byte[frame.Length];
        var held = session.GetStore(frame.Direction).TryCopyTo(frame.Offset, bytes);
        return frame.Kind + " " + frame.Type + " " + frame.Length + " " + frame.StartTicks + " " + (held ? Convert.ToHexString(bytes) : "?");
    }

    private static RecordingFile Reopen(MemoryStream saved, out CaptureSession session)
    {
        var recording = RecordingFile.Open(new MemoryStream(saved.ToArray()));
        if (recording.Sessions.Count != 1)
            throw new InvalidOperationException("A section holds exactly one session, not " + recording.Sessions.Count);
        session = recording.CreateSession(recording.Sessions[0].SessionId);
        recording.Index(session);
        return recording;
    }
}
