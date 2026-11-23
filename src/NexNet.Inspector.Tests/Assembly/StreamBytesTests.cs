using System.Buffers;
using NexNet.Inspector.Core.Assembly;

namespace NexNet.Inspector.Tests.Assembly;

public class StreamBytesTests
{
    [Test]
    public async Task Ranges_across_ops_read_back_as_written()
    {
        var bytes = new InMemoryStreamBytes(startOffset: 100);
        var expected = new byte[1000];
        new Random(3).NextBytes(expected);
        var random = new Random(4);
        for (var position = 0; position < expected.Length;)
        {
            var size = Math.Min(random.Next(1, 50), expected.Length - position);
            bytes.Append(expected.AsMemory(position, size));
            position += size;
        }

        await Assert.That(bytes.StartOffset).IsEqualTo(100);
        await Assert.That(bytes.EndOffset).IsEqualTo(1100);
        for (var start = 0; start < expected.Length; start += 37)
        {
            var length = Math.Min(200, expected.Length - start);
            await Assert.That(bytes.TryGetSequence(100 + start, length, out var sequence)).IsTrue();
            await Assert.That(sequence.ToArray()).IsEquivalentTo(expected.AsSpan(start, length).ToArray());

            var copy = new byte[length];
            await Assert.That(bytes.TryCopyTo(100 + start, copy)).IsTrue();
            await Assert.That(copy).IsEquivalentTo(expected.AsSpan(start, length).ToArray());
        }

        await Assert.That(bytes.TryGetSequence(99, 2, out _)).IsFalse();
        await Assert.That(bytes.TryGetSequence(1099, 2, out _)).IsFalse();
    }

    [Test]
    public async Task Trimmed_ops_are_no_longer_available()
    {
        var bytes = new InMemoryStreamBytes();
        for (var i = 0; i < 200; i++)
            bytes.Append(new byte[] { (byte)i, (byte)i });

        bytes.TrimBefore(201);
        await Assert.That(bytes.StartOffset).IsEqualTo(200);
        await Assert.That(bytes.TryGetSegment(199, out _)).IsFalse();
        await Assert.That(bytes.TryGetSegment(201, out var segment)).IsTrue();
        await Assert.That(segment.Span[0]).IsEqualTo((byte)100);
        await Assert.That(bytes.TryGetSegment(399, out segment)).IsTrue();
        await Assert.That(segment.Span[0]).IsEqualTo((byte)199);
    }
}
