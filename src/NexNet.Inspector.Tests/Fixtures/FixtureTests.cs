using System.Buffers;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Fixtures;

/// <summary>
/// The checked-in recordings (Fixtures/*.nntl) still read with the current reader, parser, assembler and decoder, and
/// still hold what the screens are built on. When the Chat nexus or the format changes, regenerate them with
/// <c>dotnet run --project src/NexNet.Inspector.Sample -c Release -- fixtures</c>.
/// </summary>
public class FixtureTests
{
    private static readonly Lazy<Dictionary<FixtureKind, RecordingAnalysis>> Analyses = new(() =>
        Sample.Fixtures.All.ToDictionary(k => k, k => RecordingAnalysis.Read(Bytes(k))));

    public static IEnumerable<FixtureKind> Kinds() => Sample.Fixtures.All;

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task Fixture_passes_its_checks(FixtureKind kind)
    {
        var problems = Sample.Fixtures.Check(kind, Analyses.Value[kind]);
        await Assert.That(problems).IsEmpty().Because(string.Join("; ", problems));
        await Assert.That(Bytes(kind).Length).IsLessThan(1024 * 1024);
    }

    [Test]
    [MethodDataSource(nameof(Kinds))]
    public async Task Fixture_reads_cleanly(FixtureKind kind)
    {
        using var reader = NntlReader.Open(new MemoryStream(Bytes(kind)));
        var truncated = kind == FixtureKind.Truncated;
        await Assert.That(reader.IndexSource).IsEqualTo(truncated ? NntlIndexSource.Scan : NntlIndexSource.Footer);
        await Assert.That(reader.Damage.Select(d => d.Kind).ToArray())
            .IsEquivalentTo(truncated ? [NntlDamageKind.TornTail] : Array.Empty<NntlDamageKind>());
        await Assert.That(reader.Header.FormatVersion).IsEqualTo(NntlFormat.Version);

        var expectedSessions = kind switch
        {
            FixtureKind.ChatServer or FixtureKind.ChatClient or FixtureKind.ChatClientVariant => 1,
            FixtureKind.FindingsSlow or FixtureKind.FindingsStall or FixtureKind.FindingsOrphan => 1,
            FixtureKind.LegacySchema => 2,
            _ => 3,
        };
        await Assert.That(reader.Sessions.Count).IsEqualTo(expectedSessions);
        await Assert.That(reader.Index.GetEntries(NntlBlockKind.Schema).Length).IsEqualTo(1);

        // Every indexed block reads and decodes.
        foreach (var entry in reader.Index.ToFileOrder())
        {
            var block = reader.ReadBlock(entry.Offset);
            await Assert.That(block.Kind).IsEqualTo(entry.Kind);
            switch (block.Kind)
            {
                case NntlBlockKind.TransportOp:
                    await Assert.That(NntlCodec.DecodeTransportOp(block.Body).SessionId).IsEqualTo(entry.SessionId);
                    break;
                case NntlBlockKind.Gap:
                    await Assert.That(NntlCodec.DecodeGap(block.Body).DroppedOps).IsGreaterThan(0);
                    break;
                case NntlBlockKind.Snapshot:
                    await Assert.That(NntlCodec.DecodeSnapshot(block.Body).Version).IsEqualTo(NntlSnapshot.CurrentVersion);
                    break;
                case NntlBlockKind.SessionClose:
                    await Assert.That(NntlCodec.DecodeSessionClose(block.Body).SessionId).IsEqualTo(entry.SessionId);
                    break;
            }
        }

        // Every session-open and close pair (the truncated copy lost its last close).
        var closes = reader.Index.Groups.Where(g => g.Kind == NntlBlockKind.SessionClose).Sum(g => g.Entries.Length);
        await Assert.That(closes).IsEqualTo(truncated ? expectedSessions - 1 : expectedSessions);
    }

    [Test]
    public async Task Chat_server_recording_holds_every_screen_entity()
    {
        var session = Analyses.Value[FixtureKind.ChatServer].Sessions.Single();
        var assembler = session.Assembler;
        var decoder = session.Decoder;
        await Assert.That(session.Open.Role).IsEqualTo(NntlSessionRole.Server);
        await Assert.That(SchemaHash.Matches(session.Open.SchemaHash.Span, Sample.Fixtures.SchemaHashOf<ChatServerNexus>())).IsTrue();
        await Assert.That(session.Schema).IsNotNull();

        // Handshake: preamble both ways, both greetings, and the 96-byte token redacted to its marker.
        await Assert.That(session.InboundFromInception && session.OutboundFromInception).IsTrue();
        var inboundFrames = assembler.GetFrames(TapDirection.Inbound).ToArray();
        await Assert.That(inboundFrames[0].Kind).IsEqualTo(FrameKind.Preamble);
        await Assert.That(inboundFrames[1].Type).IsEqualTo(MessageType.ClientGreeting);
        var clientGreeting = assembler.Greetings.ToArray().Single(g => g.Kind == GreetingKind.Client);
        await Assert.That(assembler.TryGetPayload(clientGreeting.Token, out var token)).IsTrue();
        await Assert.That(token.ToArray()).IsEquivalentTo(Marker(ChatScenario.Token));
        await Assert.That(session.InboundRedactedBytes).IsEqualTo(96);
        await Assert.That(assembler.Greetings.ToArray().Count(g => g.Kind == GreetingKind.Server)).IsEqualTo(1);

        // The canceled SendMessage, its cancellation frame flagged X.
        var canceled = session.ClientCalls("SendMessage").Single(i => i.CancellationSeq >= 0);
        await Assert.That(assembler.TryGetFrame(TapDirection.Inbound, canceled.CancellationSeq, out var cancel)).IsTrue();
        await Assert.That(cancel.Type).IsEqualTo(MessageType.InvocationCancellation);
        await Assert.That(cancel.Flags.HasFlag(FrameFlags.Cancel)).IsTrue();

        // SendAttachment: MessageStart and five chunks, with FileUpload pipe frames and a Ping between them.
        var attachment = session.ClientCalls("SendAttachment").Single();
        await Assert.That(attachment.Request.Kind).IsEqualTo(MessageRefKind.Assembled);
        var large = assembler.Messages[attachment.Request.Index];
        await Assert.That(large.Kind).IsEqualTo(AssembledMessageKind.Large);
        await Assert.That(large.State).IsEqualTo(AssemblyState.Complete);
        var fragments = new List<TraceFragment>();
        assembler.GetFragments(attachment.Request, fragments);
        await Assert.That(fragments.Count(f => f.Length > 0)).IsEqualTo(5);
        var between = new List<FrameRecord>();
        assembler.GetInterleaved(attachment.Request, between);
        await Assert.That(between.Any(f => f.Type == MessageType.Ping)).IsTrue();
        var uploadPipes = session.ClientCalls("UploadFile").Select(u => u.Pipe).ToArray();
        await Assert.That(between.Any(f => f.Target == FrameTarget.Pipe && uploadPipes.Contains(PipeIndex(assembler, f)))).IsTrue();
        var args = decoder.DecodeArguments(attachment);
        await Assert.That(args.Problem).IsEqualTo(DecodeProblem.None);
        await Assert.That(SessionAnalysis.TryFindField(args, "Data", out var data)).IsTrue();
        await Assert.That(data.ContentLength).IsEqualTo(Sample.Fixtures.AttachmentBytes);
        await Assert.That(decoder.DecodeResult(attachment).Problem).IsEqualTo(DecodeProblem.None);

        // The LogLine channel, the Users collection, five pipes.
        var tail = session.ClientCalls("TailLogs")[0];
        await Assert.That(assembler.Pipes[tail.Pipe].Content).IsEqualTo(PipeContent.Channel);
        var lines = assembler.ChannelItems.ToArray().Where(i => i.Pipe == tail.Pipe).Select(i => decoder.DecodeChannelItem(i)).ToArray();
        await Assert.That(lines.Length).IsEqualTo(Sample.Fixtures.LogLines);
        await Assert.That(lines.All(l => l.Problem == DecodeProblem.None)).IsTrue();
        await Assert.That(SessionAnalysis.TryFindField(lines[0], "Source", out var source) && lines[0].GetText(source) == "chat.rooms").IsTrue();
        var users = assembler.Collections.ToArray().Single();
        await Assert.That(decoder.TryGetCollection(users.CollectionId, out var collection)).IsTrue();
        await Assert.That(collection!.Name).IsEqualTo("Users");
        await Assert.That(assembler.CollectionMessages.ToArray().Any(m => m.Kind == CollectionMessageKind.Insert)).IsTrue();
        await Assert.That(assembler.Pipes.Length).IsGreaterThanOrEqualTo(5);

        // The Drop-mode gap: ops the stalled recorder dropped, reported per direction.
        await Assert.That(session.Gaps.Count).IsGreaterThan(0);
        await Assert.That(session.Gaps.Sum(g => g.DroppedOps)).IsGreaterThan(0);
        await Assert.That(session.Gaps.All(g => g.ToTicks >= g.FromTicks && g.DroppedBytes > 0)).IsTrue();
        await Assert.That(session.Close?.Kind).IsEqualTo(NntlCloseKind.Disconnected);
    }

    [Test]
    public async Task Chat_attachment_fits_the_inspect_live_window_with_a_pipe_write_and_a_ping_between_its_chunks()
    {
        var recording = Analyses.Value[FixtureKind.ChatServer];
        var session = recording.Sessions.Single();
        var assembler = session.Assembler;
        var attachment = session.ClientCalls("SendAttachment").Single();
        var frequency = recording.Header.StopwatchFrequency;

        // The chunks are paced a few milliseconds apart, not a quarter second: the whole call fits the screen's 50 ms.
        var span = Sample.Fixtures.AttachmentMilliseconds(assembler, attachment, frequency);
        await Assert.That(span).IsLessThanOrEqualTo(Sample.Fixtures.MaxAttachmentMilliseconds);

        // Between two chunks: a FileUpload write and a ping (the client pings every 15 ms while recording).
        var fragments = new List<TraceFragment>();
        assembler.GetFragments(attachment.Request, fragments);
        var chunks = fragments.Where(f => f.Length > 0).Select(f => f.Seq).ToArray();
        var between = new List<FrameRecord>();
        assembler.GetInterleaved(attachment.Request, between);
        await Assert.That(between.Any(f => f.Target == FrameTarget.Pipe && f.Seq > chunks[0] && f.Seq < chunks[^1])).IsTrue();
        await Assert.That(between.Any(f => f.Type == MessageType.Ping && f.Seq > chunks[0] && f.Seq < chunks[^1])).IsTrue();

        // The dropped-events gap follows the call closely enough to share its window.
        assembler.TryGetFrame(attachment.Request.Direction, attachment.Request.Seq, out var start);
        var gap = session.Gaps.Where(g => g.FromTicks >= start.StartTicks).Min(g => g.FromTicks);
        await Assert.That((gap - start.StartTicks) * 1000.0 / frequency).IsLessThan(50);
    }

    [Test]
    public async Task Chat_client_recording_is_the_same_session_from_the_other_side()
    {
        var client = Analyses.Value[FixtureKind.ChatClient].Sessions.Single();
        var server = Analyses.Value[FixtureKind.ChatServer].Sessions.Single();
        await Assert.That(client.Open.Role).IsEqualTo(NntlSessionRole.Client);
        await Assert.That(client.Gaps).IsEmpty();
        await Assert.That(client.OutboundRedactedBytes).IsEqualTo(96);

        foreach (var method in new[] { "Join", "SendMessage", "SendAttachment", "UploadFile", "TailLogs" })
            await Assert.That(client.ClientCalls(method).Length).IsEqualTo(server.ClientCalls(method).Length).Because(method);

        // The client sent the attachment: its chunks are outbound here.
        var attachment = client.ClientCalls("SendAttachment").Single();
        await Assert.That(attachment.Direction).IsEqualTo(TapDirection.Outbound);
        var fragments = new List<TraceFragment>();
        client.Assembler.GetFragments(attachment.Request, fragments);
        await Assert.That(fragments.Count(f => f.Length > 0)).IsEqualTo(5);

        // Lossless: the client read every log line of both TailLogs calls, burst included.
        var tails = client.ClientCalls("TailLogs");
        var items = client.Assembler.ChannelItems.ToArray().Count(i => i.Pipe == tails[1].Pipe);
        await Assert.That(items).IsGreaterThan(Sample.Fixtures.LogLines);
    }

    [Test]
    public async Task Server_all_records_three_sessions_one_from_a_snapshot()
    {
        var recording = Analyses.Value[FixtureKind.ServerAll];
        await Assert.That(recording.Sessions.Count).IsEqualTo(3);
        var attached = recording.Sessions.Single(s => s.HasSnapshot);
        await Assert.That(attached.InboundFromInception || attached.OutboundFromInception).IsFalse();
        await Assert.That(attached.Assembler.Greetings.Length).IsEqualTo(0);
        foreach (var session in recording.Sessions.Where(s => !s.HasSnapshot))
        {
            await Assert.That(session.InboundFromInception && session.OutboundFromInception).IsTrue();
            await Assert.That(session.Assembler.Greetings.Length).IsEqualTo(2);
            await Assert.That(session.ClientCalls("SendMessage").Count(i => i.CancellationSeq >= 0)).IsEqualTo(1);
        }

        await Assert.That(recording.Sessions.Select(s => s.Open.SessionId).Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Legacy_recording_schema_differs_from_the_current_chat_schema()
    {
        var recording = Analyses.Value[FixtureKind.LegacySchema];
        var current = NexusSchema.Of<ChatServerNexus>();
        foreach (var session in recording.Sessions)
        {
            await Assert.That(SchemaHash.Matches(session.Open.SchemaHash.Span, current.Hash)).IsFalse();
            await Assert.That(session.Schema!.Hash).IsNotEqualTo(current.Hash);

            // Its own embedded schema still decodes it.
            var send = session.ClientCalls("SendMessage")[0];
            var args = session.Decoder.DecodeArguments(send);
            await Assert.That(SessionAnalysis.TryFindField(args, "text", out var text)).IsTrue();
            await Assert.That(args.GetText(text)).IsEqualTo("hello from v1");
        }
    }

    [Test]
    public async Task Truncated_recording_reads_by_scanning_and_repairs()
    {
        var bytes = Bytes(FixtureKind.Truncated);
        var complete = Analyses.Value[FixtureKind.ServerAll];
        var truncated = Analyses.Value[FixtureKind.Truncated];
        await Assert.That(truncated.Sessions.Select(s => s.Open.SessionId)).IsEquivalentTo(complete.Sessions.Select(s => s.Open.SessionId));

        using var copy = new MemoryStream();
        copy.Write(bytes);
        var result = NntlFile.RebuildIndex(copy);
        await Assert.That(result.TruncatedBytes).IsGreaterThan(0);
        using var repaired = NntlReader.Open(new MemoryStream(copy.ToArray()));
        await Assert.That(repaired.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        await Assert.That(repaired.Sessions.Count).IsEqualTo(3);
    }

    private static byte[] Bytes(FixtureKind kind) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", Sample.Fixtures.FileName(kind)));

    private static byte[] Marker(byte[] token)
    {
        var marker = new byte[token.Length];
        InspectorRedaction.WriteMarker(token, marker);
        return marker;
    }

    private static int PipeIndex(MessageAssembler assembler, in FrameRecord frame)
    {
        var link = assembler.GetLink(frame.Direction, frame.Seq);
        return link.Entity == EntityKind.Pipe ? link.EntityIndex : -1;
    }
}

/// <summary>The regeneration command still produces recordings that pass the fixture checks.</summary>
[NotInParallel(nameof(FixtureGeneratorTests))]
public class FixtureGeneratorTests
{
    [Test]
    public async Task Generator_produces_valid_fixtures()
    {
        var log = new StringWriter();
        Dictionary<FixtureKind, byte[]> files;
        try
        {
            files = await FixtureGenerator.GenerateAsync(log).WaitAsync(TimeSpan.FromMinutes(3));
        }
        catch (InvalidOperationException e)
        {
            // Which checks the attempts missed.
            throw new InvalidOperationException($"{e.Message} log: {log}", e);
        }

        foreach (var kind in Sample.Fixtures.All)
        {
            var problems = Sample.Fixtures.Check(kind, RecordingAnalysis.Read(files[kind]));
            await Assert.That(problems).IsEmpty().Because($"{kind}: {string.Join("; ", problems)}; log: {log}");
        }
    }
}
