using NexNet.Inspector.Core.Format;
using TUnit.Assertions.Enums;

namespace NexNet.Inspector.Tests.Format;

public class NntlRecoveryTests
{
    private const int LastBlocksToCut = 5;

    [Test]
    public async Task Truncating_anywhere_in_the_last_blocks_recovers_every_complete_block()
    {
        var recording = NntlTestData.Build();
        var file = recording.File;
        var (starts, ends) = BlockBounds(file);
        var indexOffset = NntlTestData.IndexOffset(file);
        var trailerStart = file.Length - NntlFormat.TrailerSize;

        // Every place a truncated file can end and still be clean: a content block end, the index end.
        var cleanCuts = new HashSet<long>(ends) { trailerStart };
        var tornStarts = starts.Concat([indexOffset, trailerStart]).ToArray();

        var failures = new List<string>();
        for (var cut = starts[^LastBlocksToCut]; cut < file.Length; cut++)
        {
            using var reader = NntlReader.Open(new MemoryStream(file, 0, (int)cut));
            var complete = ends.Count(e => e <= cut);
            var actual = NntlTestData.ReadAll(reader);
            if (reader.IndexSource != NntlIndexSource.Scan)
                failures.Add($"{cut}: index came from {reader.IndexSource}");
            if (!actual.SequenceEqual(recording.Expected.Take(complete)))
                failures.Add($"{cut}: recovered {actual.Length} blocks, expected the first {complete}");

            NntlDamage[] expectedDamage = cleanCuts.Contains(cut)
                ? []
                : [TornAt(tornStarts.Where(s => s < cut).Max(), cut)];
            if (!reader.Damage.SequenceEqual(expectedDamage))
                failures.Add($"{cut}: damage [{string.Join(", ", reader.Damage)}], expected [{string.Join(", ", expectedDamage)}]");
        }

        await Assert.That(string.Join("\n", failures)).IsEqualTo("");
    }

    [Test]
    public async Task A_corrupt_byte_in_the_middle_is_skipped_and_reported()
    {
        var recording = NntlTestData.Build();
        var (starts, ends) = BlockBounds(recording.File);
        var victim = starts.Length / 2;

        // A byte in the body, the sync marker, the kind, the length and the checksum each break the block.
        foreach (var at in new long[] { 30, 1, 4, 9, 13 })
        {
            var file = recording.File.ToArray();
            file[starts[victim] + at] ^= 0x5A;
            var withoutFooter = file.AsSpan(0, (int)NntlTestData.IndexOffset(file)).ToArray();
            var expected = recording.Expected.Where((_, i) => i != victim).ToArray();
            var corrupt = new NntlDamage(NntlDamageKind.Corrupt, starts[victim], ends[victim] - starts[victim]);

            foreach (var (bytes, mode) in new[] { (withoutFooter, NntlOpenMode.PreferFooter), (file, NntlOpenMode.Scan) })
            {
                using var reader = NntlReader.Open(new MemoryStream(bytes), mode);
                await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Scan);
                await Assert.That(reader.Damage.ToArray()).IsEquivalentTo(new[] { corrupt }, CollectionOrdering.Matching);
                await Assert.That(string.Join("\n", NntlTestData.ReadAll(reader))).IsEqualTo(string.Join("\n", expected));
            }
        }
    }

    [Test]
    public async Task With_an_intact_footer_a_corrupt_block_is_skipped_on_read()
    {
        var recording = NntlTestData.Build();
        var file = recording.File.ToArray();
        using var original = NntlReader.Open(new MemoryStream(recording.File));
        var session = NntlTestData.SessionIds[0];
        var ops = original.Index.GetEntries(NntlBlockKind.TransportOp, session).ToArray();
        file[ops[2].Offset + 20] ^= 0xFF;

        using var reader = NntlReader.Open(new MemoryStream(file));
        await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        await Assert.That(reader.TryReadBlock(ops[2].Offset, out _)).IsFalse();
        await Assert.That(() => reader.ReadBlock(ops[2].Offset)).ThrowsExactly<NntlFormatException>();

        var seen = new List<long>();
        foreach (var op in reader.ReadTransportOps(session))
            seen.Add(op.Ticks);
        await Assert.That(seen.ToArray()).IsEquivalentTo(ops.Where((_, i) => i != 2).Select(e => e.Ticks).ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildIndex_writes_the_footer_the_writer_wrote()
    {
        var recording = NntlTestData.Build();
        var footerLength = recording.File.Length - NntlTestData.IndexOffset(recording.File);

        // Intact file: the old footer is replaced by an identical one.
        var intact = new MemoryStream();
        intact.Write(recording.File);
        var result = NntlFile.RebuildIndex(intact);
        await Assert.That(intact.ToArray().AsSpan().SequenceEqual(recording.File)).IsTrue();
        await Assert.That(result.TruncatedBytes).IsEqualTo(footerLength);
        await Assert.That(result.Damage.Count).IsEqualTo(0);
        await Assert.That(result.BlockCount).IsEqualTo(recording.Expected.Length);

        // Footer lost, as after a crash just before close.
        var crashed = new MemoryStream();
        crashed.Write(recording.File.AsSpan(0, (int)NntlTestData.IndexOffset(recording.File)));
        NntlFile.RebuildIndex(crashed);
        await Assert.That(crashed.ToArray().AsSpan().SequenceEqual(recording.File)).IsTrue();
    }

    [Test]
    public async Task RebuildIndex_drops_a_torn_tail_and_the_file_opens_from_its_footer()
    {
        var recording = NntlTestData.Build();
        var (starts, ends) = BlockBounds(recording.File);
        var cut = starts[^1] + 7;
        var torn = new MemoryStream();
        torn.Write(recording.File.AsSpan(0, (int)cut));

        var result = NntlFile.RebuildIndex(torn);
        await Assert.That(result.TruncatedBytes).IsEqualTo(7);
        await Assert.That(result.Damage.ToArray()).IsEquivalentTo(new[] { TornAt(starts[^1], cut) }, CollectionOrdering.Matching);

        torn.Position = 0;
        using var reader = NntlReader.Open(torn);
        await Assert.That(reader.IndexSource).IsEqualTo(NntlIndexSource.Footer);
        await Assert.That(string.Join("\n", NntlTestData.ReadAll(reader)))
            .IsEqualTo(string.Join("\n", recording.Expected.Take(ends.Length - 1)));
    }

    private static NntlDamage TornAt(long start, long cut) => new(NntlDamageKind.TornTail, start, cut - start);

    /// <summary>Start and end offsets of every content block (index and trailer excluded), in file order.</summary>
    private static (long[] Starts, long[] Ends) BlockBounds(byte[] file)
    {
        using var reader = NntlReader.Open(new MemoryStream(file));
        var refs = reader.Index.ToFileOrder();
        var starts = refs.Select(r => r.Offset).ToArray();
        var ends = refs.Select(r => r.Offset + reader.ReadBlock(r.Offset).Length).ToArray();
        return (starts, ends);
    }
}
