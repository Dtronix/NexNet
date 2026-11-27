using System.Buffers;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Sample;

namespace NexNet.Inspector.Tests.Session;

/// <summary>The two <see cref="IStreamBytes"/> stores of the viewer: a live capture's ops in memory, a recording's on disk.</summary>
public class OpStoreTests
{
    [Test]
    public async Task Live_store_reads_ranges_across_ops_and_refuses_gap_bytes()
    {
        var store = new LiveOpStore();
        var expected = new byte[3000];
        new Random(5).NextBytes(expected);

        // 0..1000 in ops of up to 70 bytes, a 500-byte gap, then 1500..3000 in one large op.
        var random = new Random(6);
        long opId = 0;
        for (var position = 0; position < 1000;)
        {
            var size = Math.Min(random.Next(1, 70), 1000 - position);
            store.Append(Op(expected.AsMemory(position, size), opId++), 0);
            position += size;
        }

        store.SkipGap(500);
        store.Append(Op(expected.AsMemory(1500, 1500), opId), 0);

        await Assert.That(store.StartOffset).IsEqualTo(0);
        await Assert.That(store.EndOffset).IsEqualTo(3000);
        await Assert.That(store.ByteCount).IsEqualTo(2500);
        await Assert.That(store.Gaps.ToArray()).IsEquivalentTo(new[] { new StreamRange(1000, 500) });
        for (var start = 0; start < 900; start += 23)
        {
            await Assert.That(store.TryGetSequence(start, 100, out var sequence)).IsTrue();
            await Assert.That(sequence.ToArray()).IsEquivalentTo(expected.AsSpan(start, 100).ToArray());
        }

        await Assert.That(store.TryGetSegment(1200, out _)).IsFalse();
        await Assert.That(store.TryGetSequence(990, 20, out _)).IsFalse();
        await Assert.That(store.TryGetSequence(1500, 1500, out var tail)).IsTrue();
        await Assert.That(tail.ToArray()).IsEquivalentTo(expected.AsSpan(1500).ToArray());
        await Assert.That(store.TryFindOp(1600, out var index)).IsTrue();
        await Assert.That(store.Ops[index].Offset).IsEqualTo(1500);
    }

    [Test]
    public async Task Live_store_copies_borrowed_bytes_and_keeps_redactions()
    {
        var store = new LiveOpStore();
        var buffer = new byte[] { 1, 2, 3, 4, 5, 6 };
        store.Append(new NntlTransportOp(1, TapDirection.Inbound, 0, 10, NntlTransportOpFlags.Redacted,
            new[] { new NntlByteRange(2, 3) }, buffer), 0);
        buffer.AsSpan().Clear();

        await Assert.That(store.TryGetSegment(0, out var segment)).IsTrue();
        await Assert.That(segment.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 });
        await Assert.That(store.Redactions.ToArray()).IsEquivalentTo(new[] { new StreamRange(2, 3) });
    }

    [Test]
    public async Task Live_store_drops_the_oldest_ops_past_its_bound()
    {
        var store = new LiveOpStore(maxRetainedBytes: 1000);
        for (var i = 0; i < 100; i++)
            store.Append(Op(Enumerable.Repeat((byte)i, 100).ToArray(), i), 0);

        await Assert.That(store.ByteCount).IsLessThanOrEqualTo(1000);
        await Assert.That(store.StartOffset).IsEqualTo(9000);
        await Assert.That(store.TrimmedBytes).IsEqualTo(9000);
        await Assert.That(store.TryGetSegment(8999, out _)).IsFalse();
        await Assert.That(store.TryGetSegment(9950, out var segment)).IsTrue();
        await Assert.That(segment.Span[0]).IsEqualTo((byte)99);
    }

    [Test]
    public async Task Recording_store_reads_back_what_the_ops_carried_with_a_bounded_cache()
    {
        using var recording = RecordingFile.Open(SessionFixtures.Path(FixtureKind.ChatClient));
        var open = recording.Sessions[0];

        // A cache far smaller than the session, so reads go back to the file.
        var store = new RecordingOpStore(recording.Reader, cacheCapacity: 4096);
        var expected = new InMemoryStreamBytes();
        var unreadable = 0;
        foreach (var entry in recording.Reader.Index.GetEntries(NntlBlockKind.TransportOp, open.SessionId).ToArray())
        {
            if (!recording.Reader.TryReadTransportOp(entry.Offset, out var op))
            {
                unreadable++;
                continue;
            }

            if (op.Direction != TapDirection.Outbound)
                continue;
            store.Append(op, entry.Offset);
            expected.Append(op.Bytes);
        }

        await Assert.That(unreadable).IsEqualTo(0);

        await Assert.That(store.EndOffset).IsEqualTo(expected.EndOffset);
        await Assert.That(store.EndOffset).IsGreaterThan(300_000);
        await Assert.That(store.CachedBytes).IsLessThanOrEqualTo(4096);

        var random = new Random(9);
        for (var i = 0; i < 200; i++)
        {
            var offset = random.NextInt64(0, store.EndOffset - 1);
            var length = (int)Math.Min(store.EndOffset - offset, random.Next(1, 2000));
            await Assert.That(store.TryGetSequence(offset, length, out var actual)).IsTrue();
            await Assert.That(expected.TryGetSequence(offset, length, out var want)).IsTrue();
            await Assert.That(actual.ToArray()).IsEquivalentTo(want.ToArray());
        }

        await Assert.That(store.CacheMisses).IsGreaterThan(0);
        await Assert.That(store.CachedBytes).IsLessThanOrEqualTo(4096);
    }

    private static NntlTransportOp Op(ReadOnlyMemory<byte> bytes, long opId)
        => new(1, TapDirection.Inbound, opId, 1000 + opId, NntlTransportOpFlags.None, ReadOnlyMemory<NntlByteRange>.Empty, bytes);
}

internal static class SessionFixtures
{
    public static string Path(FixtureKind kind)
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", Sample.Fixtures.FileName(kind));
}
