using NexNet.Inspector.Core.Format;
using TUnit.Assertions.Enums;
using NexNet.Inspector.Core.Model;

namespace NexNet.Inspector.Tests.Format;

public class NntlRandomAccessTests
{
    [Test]
    public async Task Blocks_read_by_offset_in_any_order()
    {
        var recording = NntlTestData.Build();
        using var reader = NntlReader.Open(new MemoryStream(recording.File));
        var refs = reader.Index.ToFileOrder();

        for (var i = refs.Length - 1; i >= 0; i -= 3)
        {
            var block = reader.ReadBlock(refs[i].Offset);
            await Assert.That(block.Kind).IsEqualTo(refs[i].Kind);
            await Assert.That(NntlTestData.Describe(block)).IsEqualTo(recording.Expected[i]);
        }

        await Assert.That(reader.TryReadBlock(refs[3].Offset + 1, out _)).IsFalse();
        await Assert.That(reader.TryReadBlock(reader.Length + 10, out _)).IsFalse();
    }

    [Test]
    public async Task Transport_ops_enumerate_by_tick_range()
    {
        var ms = new MemoryStream();
        using (var writer = new NntlWriter(ms, NntlTestData.Header, leaveOpen: true))
        {
            for (var i = 0; i < 100; i++)
            {
                writer.WriteTransportOp(1, TapDirection.Inbound, i, i * 100, [(byte)i]);
                writer.WriteTransportOp(2, TapDirection.Outbound, i, i * 100 + 50, [(byte)i]);
            }

            // Session 3 queued an op out of tick order, so its lookups cannot binary search.
            writer.WriteTransportOp(3, TapDirection.Inbound, 0, 500, [0]);
            writer.WriteTransportOp(3, TapDirection.Outbound, 1, 300, [1]);
            writer.WriteTransportOp(3, TapDirection.Inbound, 2, 900, [2]);
        }

        using var reader = NntlReader.Open(new MemoryStream(ms.ToArray()));
        await Assert.That(Ticks(reader, 1, 250, 650)).IsEquivalentTo(new long[] { 300, 400, 500, 600 }, CollectionOrdering.Matching);
        await Assert.That(Ticks(reader, 2, 250, 650)).IsEquivalentTo(new long[] { 250, 350, 450, 550, 650 }, CollectionOrdering.Matching);
        await Assert.That(Ticks(reader, 1, 9_900, long.MaxValue)).IsEquivalentTo(new long[] { 9_900 }, CollectionOrdering.Matching);
        await Assert.That(Ticks(reader, 1, 20_000, 30_000)).IsEmpty();
        await Assert.That(Ticks(reader, 1, long.MinValue, long.MaxValue).Length).IsEqualTo(100);
        await Assert.That(Ticks(reader, 99, long.MinValue, long.MaxValue)).IsEmpty();

        await Assert.That(reader.Index.FindGroup(NntlBlockKind.TransportOp, 1)!.TicksAscending).IsTrue();
        await Assert.That(reader.Index.FindGroup(NntlBlockKind.TransportOp, 3)!.TicksAscending).IsFalse();
        await Assert.That(Ticks(reader, 3, 300, 600)).IsEquivalentTo(new long[] { 500, 300 }, CollectionOrdering.Matching);
    }

    private static long[] Ticks(NntlReader reader, long session, long from, long to)
    {
        var ticks = new List<long>();
        foreach (var op in reader.ReadTransportOps(session, from, to))
        {
            ticks.Add(op.Ticks);
            if (op.Bytes.Span[0] != (byte)op.OpId)
                throw new InvalidOperationException("Op bytes do not match the op.");
        }

        return ticks.ToArray();
    }
}
