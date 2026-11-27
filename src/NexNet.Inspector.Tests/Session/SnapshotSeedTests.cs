using System.Buffers;
using NexNet.Collections;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;
using NexNet.Inspector.Tests.Assembly;

namespace NexNet.Inspector.Tests.Session;

/// <summary>An attach mid-session decodes from its first frame: the snapshot seeds pipes, collections and large messages.</summary>
public class SnapshotSeedTests
{
    [Test]
    public async Task Seeded_pipes_collections_and_large_messages_resolve_their_first_frames()
    {
        var inbound = new LiveOpStore();
        var outbound = new LiveOpStore();
        var assembler = new MessageAssembler(inbound, outbound, NntlSessionRole.Server);
        assembler.Seed(new SnapshotSeed(
            [
                new NntlPipeState(0x0201, NntlPipeStateFlags.Ready, 65536, 0, 0, 0),
                new NntlPipeState(0x0302, NntlPipeStateFlags.Ready | NntlPipeStateFlags.Closing, 65536, 0, 0, 0),
                new NntlPipeState(0x0004, NntlPipeStateFlags.None, 65536, 0, 0, 0),
            ],
            new NntlInvocationState(0, 1, 0, 10, 10, 0, 0, 0,
                [new NntlLargeMessageState(TapDirection.Inbound, IsResult: false, InvocationId: 5, Length: 20, Transferred: 8)]),
            [new NntlCollectionState(4, NexusCollectionKind.List, NexusCollectionMode.ServerToClient, NexusCollectionState.Connected, 3, 17, [0x0302])]));

        var pipes = assembler.Pipes.ToArray();
        await Assert.That(pipes.Length).IsEqualTo(2);
        await Assert.That(pipes.All(p => p.FromSnapshot && p.OpenSeq < 0)).IsTrue();
        await Assert.That(pipes[1].Lifecycle).IsEqualTo(PipeLifecycle.Closing);
        await Assert.That(pipes[1].Content).IsEqualTo(PipeContent.Collection);
        var collection = assembler.Collections.ToArray().Single();
        await Assert.That(collection.CollectionId).IsEqualTo((ushort)4);
        await Assert.That(collection.LastVersion).IsEqualTo(17);
        await Assert.That(assembler.Invocations.Length).IsEqualTo(1);

        var script = new WireScript()
            .PipeWrite(TapDirection.Inbound, 0x0201, [1, 2, 3])
            .Chunk(TapDirection.Inbound, 5, LargeMessageKind.Argument, new byte[12])
            .PipeWrite(TapDirection.Inbound, 0x0302, [9]);
        var parser = new FrameParser(TapDirection.Inbound, expectPreamble: false);
        long op = 0;
        foreach (var (_, frame) in script.Frames)
        {
            inbound.Append(GapResyncTests.Op(frame, op), 0);
            parser.Push(op, op++, frame, assembler);
        }

        // Frames of pipes opened before the attach belong to the seeded pipes, not to new unknown ones.
        await Assert.That(assembler.Pipes.Length).IsEqualTo(2);
        await Assert.That(assembler.GetLink(TapDirection.Inbound, 0)).IsEqualTo(assembler.GetLink(TapDirection.Inbound, 0) with { Entity = EntityKind.Pipe, EntityIndex = 0 });
        await Assert.That(assembler.GetLink(TapDirection.Inbound, 2).EntityIndex).IsEqualTo(1);
        await Assert.That(assembler.Pipes[0].InboundBytes).IsEqualTo(3);

        // The large message continues at offset 8 and ends at its announced 20 bytes.
        var large = assembler.Messages.ToArray().Single(m => m.Kind == AssembledMessageKind.Large);
        await Assert.That(large.Length).IsEqualTo(20);
        await Assert.That(large.Invocation).IsEqualTo(0);
        await Assert.That(assembler.GetLink(TapDirection.Inbound, 1).EntityIndex).IsEqualTo(0);
        await Assert.That(assembler.TryGetPayload(new PayloadRange(large.Ref, 8, 12), out var known)).IsTrue();
        await Assert.That(known.Length).IsEqualTo(12);
        await Assert.That(assembler.TryGetPayload(new PayloadRange(large.Ref, 0, 10), out _)).IsFalse();

        // A new stream with the same ID is a new message, not more of the seeded one.
        await Assert.That(assembler.Invocations[0].Request).IsEqualTo(large.Ref);
    }

    [Test]
    public async Task Seeding_after_the_first_frame_is_refused()
    {
        var assembler = new MessageAssembler(new LiveOpStore(), new LiveOpStore());
        assembler.Add(new FrameRecord { Kind = FrameKind.Frame, Type = Messages.MessageType.Ping, Length = 1, HeaderLength = 1 });
        await Assert.That(() => assembler.Seed(SnapshotSeed.Empty)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task The_attached_session_of_a_record_all_starts_from_its_snapshot()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ServerAll));
        CaptureSession? attached = null;
        foreach (var open in recording.Sessions)
        {
            var session = recording.CreateSession(open.SessionId);
            recording.Index(session);
            if (session.Snapshot is not null)
                attached = session;
        }

        await Assert.That(attached).IsNotNull();
        var s = attached!;
        await Assert.That(s.IsFromInception(TapDirection.Inbound) || s.IsFromInception(TapDirection.Outbound)).IsFalse();
        await Assert.That(s.MalformedSnapshotSections).IsEqualTo(0);
        await Assert.That(s.FrameCount).IsGreaterThan(0);
        await Assert.That(s.GetParser(TapDirection.Inbound)!.IsFaulted || s.GetParser(TapDirection.Outbound)!.IsFaulted).IsFalse();

        // Every open pipe the snapshot named is known before its first frame.
        var seededIds = s.Snapshot!.Pipes.Where(p => (p.Flags & NntlPipeStateFlags.Ready) != 0).Select(p => p.Id).ToArray();
        var seeded = s.Assembler.Pipes.ToArray().Where(p => p.FromSnapshot).Select(p => p.PipeId).ToArray();
        await Assert.That(seeded).IsEquivalentTo(seededIds);
        foreach (var c in s.Snapshot.Collections.Where(c => c.PipeIds.Length > 0))
            await Assert.That(s.Assembler.Collections.ToArray().Any(r => r.CollectionId == c.Id)).IsTrue();
    }
}
