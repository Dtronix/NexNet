using System.Buffers;
using NexNet.Inspector.Core.Format;
using TUnit.Assertions.Enums;
using NexNet.Inspector.Core.Model;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Format;

public class NntlRoundTripTests
{
    [Test]
    public async Task Every_block_kind_round_trips_with_interleaved_sessions()
    {
        var recording = NntlTestData.Build();
        using var reader = NntlReader.Open(new MemoryStream(recording.File));

        await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        await Assert.That(reader.Damage.Count).IsEqualTo(0);
        await Assert.That(string.Join("\n", NntlTestData.ReadAll(reader))).IsEqualTo(string.Join("\n", recording.Expected));
        await Assert.That(NntlTestData.Describe(reader.Header)).IsEqualTo(NntlTestData.Describe(NntlTestData.Header));
        await Assert.That(reader.Index.SessionIds.ToArray()).IsEquivalentTo(NntlTestData.SessionIds, CollectionOrdering.Matching);
        await Assert.That(reader.Sessions.Count).IsEqualTo(3);
        await Assert.That(reader.Sessions[0].SchemaHash.ToArray()).IsEquivalentTo(NntlTestData.SchemaHash, CollectionOrdering.Matching);
        await Assert.That(reader.Sessions[2].SchemaHash.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Scanning_builds_the_same_index_as_the_footer()
    {
        var recording = NntlTestData.Build();
        using var footer = NntlReader.Open(new MemoryStream(recording.File));
        using var scan = NntlReader.Open(new MemoryStream(recording.File), NntlOpenMode.Scan);

        await Assert.That(scan.IndexSource).IsEqualTo(NntlIndexSource.Scan);
        await Assert.That(scan.Damage.Count).IsEqualTo(0);
        await Assert.That(scan.Index.ToFileOrder()).IsEquivalentTo(footer.Index.ToFileOrder(), CollectionOrdering.Matching);
        await Assert.That(scan.Index.Groups.Select(g => (g.Kind, g.SessionId)).ToArray())
            .IsEquivalentTo(footer.Index.Groups.Select(g => (g.Kind, g.SessionId)).ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Non_seekable_output_produces_the_same_file()
    {
        var seekable = NntlTestData.Build();
        var piped = NntlTestData.Build(output => new NonSeekableStream(output));

        await Assert.That(piped.File.AsSpan().SequenceEqual(seekable.File)).IsTrue();
        using var reader = NntlReader.Open(new MemoryStream(piped.File));
        await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        await Assert.That(string.Join("\n", NntlTestData.ReadAll(reader))).IsEqualTo(string.Join("\n", piped.Expected));
    }

    [Test]
    public async Task Schema_is_embedded_once_per_hash()
    {
        var ms = new MemoryStream();
        bool first, again, other;
        using (var writer = new NntlWriter(ms, NntlTestData.Header, leaveOpen: true))
        {
            first = writer.WriteSchema(NntlTestData.SchemaHash, NntlTestData.SchemaBytes);
            again = writer.WriteSchema(NntlTestData.SchemaHash, NntlTestData.SchemaBytes);
            other = writer.WriteSchema([1, 2, 3], [4, 5]);
        }

        using var reader = NntlReader.Open(new MemoryStream(ms.ToArray()));
        await Assert.That(first).IsTrue();
        await Assert.That(again).IsFalse();
        await Assert.That(other).IsTrue();
        await Assert.That(reader.Index.GetEntries(NntlBlockKind.Schema).Length).IsEqualTo(2);
        await Assert.That(reader.TryGetSchema(NntlTestData.SchemaHash, out var schema)).IsTrue();
        await Assert.That(schema.Schema.ToArray()).IsEquivalentTo(NntlTestData.SchemaBytes, CollectionOrdering.Matching);
        await Assert.That(reader.TryGetSchema([9, 9], out _)).IsFalse();
    }

    [Test]
    public async Task Concurrent_appends_from_many_sessions_all_land()
    {
        const int sessions = 8;
        const int opsPerSession = 400;
        var ms = new MemoryStream();
        using (var writer = new NntlWriter(ms, NntlTestData.Header, leaveOpen: true))
        {
            Parallel.For(0, sessions, s =>
            {
                for (var op = 0; op < opsPerSession; op++)
                    writer.WriteTransportOp(s + 1, (TapDirection)(op & 1), op, op, NntlTestData.OpBytes(op));
            });
        }

        using var reader = NntlReader.Open(new MemoryStream(ms.ToArray()), NntlOpenMode.Scan);
        await Assert.That(reader.Damage.Count).IsEqualTo(0);
        await Assert.That(reader.Index.SessionIds.Count).IsEqualTo(sessions);
        for (var s = 1; s <= sessions; s++)
        {
            var expected = 0L;
            var intact = true;
            foreach (var op in reader.ReadTransportOps(s))
            {
                intact &= op.OpId == expected && op.Bytes.Span.SequenceEqual(NntlTestData.OpBytes((int)expected));
                expected++;
            }

            await Assert.That(intact).IsTrue();
            await Assert.That(expected).IsEqualTo(opsPerSession);
        }
    }

    [Test]
    public async Task Header_anchor_converts_ticks_to_wall_clock()
    {
        var h = NntlTestData.Header;
        await Assert.That(h.ToUtc(h.StopwatchTicksAtAnchor).Ticks).IsEqualTo(h.WallClockUtcTicks);
        await Assert.That(h.ToUtc(h.StopwatchTicksAtAnchor + h.StopwatchFrequency * 2).Ticks)
            .IsEqualTo(h.WallClockUtcTicks + TimeSpan.TicksPerSecond * 2);

        var now = NntlFileHeader.Create("p", "v");
        await Assert.That(Math.Abs((now.ToUtc(now.StopwatchTicksAtAnchor) - DateTime.UtcNow).TotalMinutes) < 1).IsTrue();
    }

    [Test]
    public async Task Decoders_skip_fields_appended_by_a_later_revision()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(buffer);
        w.WriteArrayHeader(8);
        w.Write(7L);
        w.Write((byte)TapDirection.Inbound);
        w.Write(1L);
        w.Write(2L);
        w.Write(3L);
        w.Write(4L);
        w.Write("future field");
        w.WriteArrayHeader(1);
        w.Write(true);
        w.Flush();

        var gap = NntlCodec.DecodeGap(buffer.WrittenMemory);
        await Assert.That(gap).IsEqualTo(new NntlGap(7, TapDirection.Inbound, 1, 2, 3, 4));
    }

    [Test]
    public async Task A_stream_without_a_header_block_is_rejected()
    {
        await Assert.That(() => NntlReader.Open(new MemoryStream(new byte[64]))).ThrowsExactly<NntlFormatException>();
        await Assert.That(() => NntlReader.Open(new MemoryStream([]))).ThrowsExactly<NntlFormatException>();
    }

    [Test]
    public async Task Appends_after_close_throw()
    {
        var writer = new NntlWriter(new MemoryStream(), NntlTestData.Header);
        writer.Close();
        await Assert.That(() => writer.WriteGap(default)).ThrowsExactly<ObjectDisposedException>();
        writer.Dispose();
    }

    [Test]
    public async Task Crc32C_matches_the_check_value_and_the_software_path()
    {
        await Assert.That(Crc32C.Compute("123456789"u8)).IsEqualTo(0xE3069283u);

        var data = new byte[1031];
        new Random(5).NextBytes(data);
        for (var length = 0; length < 40; length++)
        {
            var span = data.AsSpan(3, length * 25);
            await Assert.That(Crc32C.Update(Crc32C.Seed, span)).IsEqualTo(Crc32C.UpdateSoftware(Crc32C.Seed, span));
        }
    }
}
