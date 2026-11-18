using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

public class PrimitiveArrayTests
{
    private static async Task RoundTripArray<T>(T[] values)
        where T : unmanaged
    {
        var bytes = TestHelpers.Serialize(values);
        await Assert.That(TestHelpers.Deserialize<T[]>(bytes)).IsEquivalentTo(values, CollectionOrdering.Matching);

        var list = new List<T>(values);
        await Assert.That(TestHelpers.Deserialize<List<T>>(TestHelpers.Serialize(list))).IsEquivalentTo(list, CollectionOrdering.Matching);

        await Assert.That(TestHelpers.Deserialize<Memory<T>>(TestHelpers.Serialize(new Memory<T>(values))).ToArray()).IsEquivalentTo(values, CollectionOrdering.Matching);
        await Assert.That(TestHelpers.Deserialize<ReadOnlyMemory<T>>(TestHelpers.Serialize(new ReadOnlyMemory<T>(values))).ToArray()).IsEquivalentTo(values, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AllKindsRoundTrip()
    {
        await RoundTripArray(new sbyte[] { -1, 0, 127 });
        await RoundTripArray(new short[] { -1, short.MaxValue });
        await RoundTripArray(new ushort[] { 1, ushort.MaxValue });
        await RoundTripArray(new[] { int.MinValue, 0, int.MaxValue });
        await RoundTripArray(new[] { 1u, uint.MaxValue });
        await RoundTripArray(new[] { long.MinValue, long.MaxValue });
        await RoundTripArray(new[] { 0UL, ulong.MaxValue });
        await RoundTripArray(new[] { 1.5f, float.NaN, float.NegativeInfinity });
        await RoundTripArray(new[] { 1.5d, double.Epsilon });
        await RoundTripArray(new[] { 'a', '字' });
        await RoundTripArray(new[] { (Half)1, (Half)(-2.5) });
        await RoundTripArray(Array.Empty<int>());
    }

    [Test]
    public async Task LargeArrayRoundTrips()
    {
        var values = Enumerable.Range(0, 200_000).Select(i => i * 31.0).ToArray();
        await RoundTripArray(values);
    }

    [Test]
    public async Task NullArrayIsNil()
    {
        await Assert.That(Hex(TestHelpers.Serialize<int[]?>(null))).IsEqualTo("c0");
        await Assert.That(TestHelpers.Deserialize<int[]>(new byte[] { 0xc0 })).IsNull();
    }

    [Test]
    public async Task EmptyArrayIsExtWithOnlyKindByte()
    {
        await Assert.That(Hex(TestHelpers.Serialize(Array.Empty<int>()))).IsEqualTo("d44e04");
    }

    [Test]
    public async Task KindMismatchThrows()
    {
        var bytes = TestHelpers.Serialize(new[] { 1, 2 });
        await Assert.That(() => TestHelpers.Deserialize<long[]>(bytes)).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task NonMultipleLengthThrows()
    {
        // ext8 length 4: kind Int32 + 3 data bytes.
        var bytes = new byte[] { 0xc7, 0x04, 78, (byte)PrimitiveKind.Int32, 1, 2, 3 };
        await Assert.That(() => TestHelpers.Deserialize<int[]>(bytes)).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task BigEndianSwapPathMatchesKnownVectors()
    {
        // Simulates the big-endian host path: swapping little-endian element bytes gives big-endian bytes.
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        PrimitiveArrayCodec<int>.SwapInPlace(data);
        await Assert.That(Hex(data)).IsEqualTo("0403020108070605");

        var shorts = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        PrimitiveArrayCodec<short>.SwapInPlace(shorts);
        await Assert.That(Hex(shorts)).IsEqualTo("02010403");

        var longs = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        PrimitiveArrayCodec<double>.SwapInPlace(longs);
        await Assert.That(Hex(longs)).IsEqualTo("0807060504030201");
    }

    [Test]
    public async Task ReadsAcrossSegments()
    {
        var values = Enumerable.Range(0, 1000).ToArray();
        var bytes = TestHelpers.Serialize(values);
        var reader = new MsgPackReader(Segmented(bytes, 7));
        int[]? result = null;
        PrimitiveArrayFormatter<int>.Instance.Deserialize(ref reader, ref result);
        await Assert.That(result).IsEquivalentTo(values, CollectionOrdering.Matching);
    }
}
