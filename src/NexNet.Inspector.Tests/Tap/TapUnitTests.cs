using System.Buffers;
using System.Diagnostics;
using NexNet.Diagnostics;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Tap;

/// <summary>The per-session tap driven directly: redaction across op boundaries and the cost of an unwatched session.</summary>
public class TapUnitTests
{
    private static readonly byte[] Token = "a-token-that-is-longer-than-the-sixty-four-hex-digits-of-its-sha256!"u8.ToArray();

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_token_split_across_one_byte_ops_is_redacted_in_each(bool server)
    {
        var greeting = TapTestData.ClientGreeting(Token);
        var after = new byte[] { (byte)MessageType.Ping };
        var (recording, file, inspector) = StartRecording();
        var tap = ((INexusTap)inspector).SessionStarted(Info(server))!;

        var ticks = new List<long>();
        foreach (var b in greeting)
        {
            var t = Stopwatch.GetTimestamp();
            ticks.Add(t);
            await Push(tap, server, [b], t);
        }

        await Push(tap, server, after, 77);
        tap.OnClosed(DisconnectReason.Graceful);
        await recording.StopAsync();

        var capture = TapCapture.FromRecording(file.ToArray(), out var reader);
        reader.Dispose();
        var direction = server ? TapDirection.Inbound : TapDirection.Outbound;
        var ops = capture.Ops(server ? 42 : 1, direction).ToArray();
        await Assert.That(ops.Length).IsEqualTo(greeting.Length + 1);
        await Assert.That(ops.Select(o => o.OpId)).IsEquivalentTo(Enumerable.Range(0, ops.Length).Select(i => (long)i));
        await Assert.That(ops.Select(o => o.Ticks).Take(greeting.Length)).IsEquivalentTo(ticks);

        var tokenAt = TapTestData.IndexOf(greeting, Token);
        var expected = greeting.ToArray();
        TapTestData.Marker(Token).CopyTo(expected, tokenAt);
        await Assert.That(ops.SelectMany(o => o.Bytes.ToArray()).ToArray()).IsEquivalentTo(expected.Concat(after).ToArray());
        for (var i = 0; i < ops.Length; i++)
        {
            var inToken = i >= tokenAt && i < tokenAt + Token.Length;
            NntlByteRange[] want = inToken ? [new NntlByteRange(0, 1)] : [];
            await Assert.That(ops[i].Redactions.ToArray()).IsEquivalentTo(want);
        }

        // The marker: hex SHA-256, 64 digits, then '*' for the rest of a long token.
        var marker = System.Text.Encoding.ASCII.GetString(TapTestData.Marker(Token));
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Token));
        await Assert.That(marker).IsEqualTo(hash + new string('*', Token.Length - 64));
    }

    [Test]
    public async Task A_greeting_whose_body_does_not_parse_is_redacted_whole()
    {
        var greeting = TapTestData.ClientGreeting(Token);
        greeting[11] = 0x91; // A one-element array where six are expected.
        var (recording, file, inspector) = StartRecording();
        var tap = ((INexusTap)inspector).SessionStarted(Info(server: true))!;
        await tap.OnRead(new ReadOnlySequence<byte>(greeting), 1);
        tap.OnClosed(DisconnectReason.Graceful);
        await recording.StopAsync();

        var capture = TapCapture.FromRecording(file.ToArray(), out var reader);
        reader.Dispose();
        var op = capture.Ops(42, TapDirection.Inbound).Single();
        await Assert.That(op.Redactions.ToArray()).IsEquivalentTo(new[] { new NntlByteRange(11, greeting.Length - 11) });
        await Assert.That(TapTestData.IndexOf(op.Bytes.ToArray(), Token)).IsEqualTo(-1);
    }

    [Test]
    public async Task A_short_token_gets_a_same_length_hex_marker()
    {
        byte[] token = [1, 2, 3, 4, 5];
        var marker = TapTestData.Marker(token);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(token));
        await Assert.That(System.Text.Encoding.ASCII.GetString(marker)).IsEqualTo(hash[..5]);
    }

    [Test]
    [NotInParallel]
    public async Task An_unwatched_session_allocates_nothing_per_op()
    {
        var inspector = NexusInspector.Create();
        var tap = ((INexusTap)inspector).SessionStarted(Info(server: true))!;

        // The greeting passes first: until then the client-to-server direction holds copies for redaction.
        await tap.OnRead(new ReadOnlySequence<byte>(TapTestData.ClientGreeting(Token)), 1);
        var data = new ReadOnlySequence<byte>(new byte[1024]);
        for (var i = 0; i < 1000; i++)
        {
            await tap.OnRead(data, i);
            await tap.OnWrite(data, i);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++)
        {
            var read = tap.OnRead(data, i);
            var write = tap.OnWrite(data, i);
            if (!read.IsCompletedSuccessfully || !write.IsCompletedSuccessfully)
                throw new InvalidOperationException("An unwatched session must never wait.");
        }

        // 200 000 ops: any per-op allocation would be megabytes. The runtime itself may allocate a few bytes once
        // (tiering, on-stack replacement of the loop), so a small constant is tolerated.
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsLessThan(1024);
        await inspector.DisposeAsync();
    }

    [Test]
    public async Task Snapshot_sections_round_trip()
    {
        var snapshot = new NexusSessionSnapshot
        {
            Invocations = new NexusInvocationSnapshot { Queued = 1, Executing = 2, AwaitingResults = 3, InboundCredits = 4,
                OutboundCredits = 5, CreditWaits = 6, SeparatePoolQueued = 7, SeparatePoolExecuting = 8 },
            Pipes =
            [
                new NexusPipeSnapshot { Id = 0x0102, IsReady = true, WriterCompleted = true, Closing = TimeSpan.FromSeconds(1),
                    Window = 65536, ReceiveCredit = 100, Buffered = 20, SendCredit = 300 },
            ],
            LargeMessages =
            [
                new NexusLargeMessageSnapshot { Direction = NexusDirection.Outbound, Kind = NexusLargeMessageKind.Result,
                    InvocationId = 9, Length = 1000, Transferred = 400 },
            ],
            Collections =
            [
                new NexusCollectionSnapshot { Id = 5, Kind = NexNet.Collections.NexusCollectionKind.List,
                    Mode = NexNet.Collections.NexusCollectionMode.ServerToClient,
                    State = NexNet.Collections.NexusCollectionState.Connected, Count = 3, Version = 11, PipeIds = [0x0201] },
            ],
        };

        var sections = NntlSnapshotSections.Create(snapshot);
        var pipe = NntlSnapshotSections.DecodeOpenPipes(sections[0].Data).Single();
        await Assert.That(pipe).IsEqualTo(new NntlPipeState(0x0102,
            NntlPipeStateFlags.Ready | NntlPipeStateFlags.WriterCompleted | NntlPipeStateFlags.Closing, 65536, 100, 20, 300));
        var invocations = NntlSnapshotSections.DecodeInvocations(sections[1].Data);
        await Assert.That(new[] { invocations.Queued, invocations.Executing, invocations.AwaitingResults, invocations.InboundCredits,
                invocations.OutboundCredits, invocations.CreditWaits, invocations.SeparatePoolQueued, invocations.SeparatePoolExecuting })
            .IsEquivalentTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        await Assert.That(invocations.LargeMessages.Single())
            .IsEqualTo(new NntlLargeMessageState(TapDirection.Outbound, true, 9, 1000, 400));
        var collection = NntlSnapshotSections.DecodeCollections(sections[2].Data).Single();
        await Assert.That((collection.Id, collection.Kind, collection.Mode, collection.State, collection.Count, collection.Version))
            .IsEqualTo(((ushort)5, NexNet.Collections.NexusCollectionKind.List, NexNet.Collections.NexusCollectionMode.ServerToClient,
                NexNet.Collections.NexusCollectionState.Connected, 3, 11));
        await Assert.That(collection.PipeIds).IsEquivalentTo(new ushort[] { 0x0201 });
    }

    private static (InspectorRecording Recording, MemoryStream File, NexusInspector Inspector) StartRecording()
    {
        var inspector = NexusInspector.Create(new InspectorOptions { AllowRecording = true });
        var file = new MemoryStream();
        return (inspector.RecordTo(file, leaveOpen: true, InspectorOverflow.Block), file, inspector);
    }

    private static NexusTapSessionInfo Info(bool server) =>
        new(server ? 42 : 0, server, "Test", server ? "test" : null, "127.0.0.1", 5000, Stopwatch.GetTimestamp());

    private static ValueTask Push(INexusSessionTap tap, bool server, byte[] bytes, long ticks) => server
        ? tap.OnRead(new ReadOnlySequence<byte>(bytes), ticks)
        : tap.OnWrite(new ReadOnlySequence<byte>(bytes), ticks);
}
