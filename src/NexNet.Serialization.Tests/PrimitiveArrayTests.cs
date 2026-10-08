using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

[TestFixture]
public class PrimitiveArrayTests
{
    private static void RoundTripArray<T>(T[] values)
        where T : unmanaged
    {
        var bytes = TestHelpers.Serialize(values);
        Assert.That(TestHelpers.Deserialize<T[]>(bytes), Is.EqualTo(values));

        var list = new List<T>(values);
        Assert.That(TestHelpers.Deserialize<List<T>>(TestHelpers.Serialize(list)), Is.EqualTo(list));

        Assert.That(TestHelpers.Deserialize<Memory<T>>(TestHelpers.Serialize(new Memory<T>(values))).ToArray(), Is.EqualTo(values));
        Assert.That(TestHelpers.Deserialize<ReadOnlyMemory<T>>(TestHelpers.Serialize(new ReadOnlyMemory<T>(values))).ToArray(), Is.EqualTo(values));
    }

    [Test]
    public void AllKindsRoundTrip()
    {
        RoundTripArray(new sbyte[] { -1, 0, 127 });
        RoundTripArray(new short[] { -1, short.MaxValue });
        RoundTripArray(new ushort[] { 1, ushort.MaxValue });
        RoundTripArray(new[] { int.MinValue, 0, int.MaxValue });
        RoundTripArray(new[] { 1u, uint.MaxValue });
        RoundTripArray(new[] { long.MinValue, long.MaxValue });
        RoundTripArray(new[] { 0UL, ulong.MaxValue });
        RoundTripArray(new[] { 1.5f, float.NaN, float.NegativeInfinity });
        RoundTripArray(new[] { 1.5d, double.Epsilon });
        RoundTripArray(new[] { 'a', '字' });
        RoundTripArray(new[] { (Half)1, (Half)(-2.5) });
        RoundTripArray(Array.Empty<int>());
    }

    [Test]
    public void LargeArrayRoundTrips()
    {
        var values = Enumerable.Range(0, 200_000).Select(i => i * 31.0).ToArray();
        RoundTripArray(values);
    }

    [Test]
    public void NullArrayIsNil()
    {
        Assert.That(Hex(TestHelpers.Serialize<int[]?>(null)), Is.EqualTo("c0"));
        Assert.That(TestHelpers.Deserialize<int[]>(new byte[] { 0xc0 }), Is.Null);
    }

    [Test]
    public void EmptyArrayIsExtWithOnlyKindByte()
    {
        Assert.That(Hex(TestHelpers.Serialize(Array.Empty<int>())), Is.EqualTo("d44e04"));
    }

    [Test]
    public void KindMismatchThrows()
    {
        var bytes = TestHelpers.Serialize(new[] { 1, 2 });
        Assert.Throws<NexusSerializationException>(() => TestHelpers.Deserialize<long[]>(bytes));
    }

    [Test]
    public void NonMultipleLengthThrows()
    {
        // ext8 length 4: kind Int32 + 3 data bytes.
        var bytes = new byte[] { 0xc7, 0x04, 78, (byte)PrimitiveKind.Int32, 1, 2, 3 };
        Assert.Throws<NexusSerializationException>(() => TestHelpers.Deserialize<int[]>(bytes));
    }

    [Test]
    public void BigEndianSwapPathMatchesKnownVectors()
    {
        // Simulates the big-endian host path: swapping little-endian element bytes gives big-endian bytes.
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        PrimitiveArrayCodec<int>.SwapInPlace(data);
        Assert.That(Hex(data), Is.EqualTo("0403020108070605"));

        var shorts = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        PrimitiveArrayCodec<short>.SwapInPlace(shorts);
        Assert.That(Hex(shorts), Is.EqualTo("02010403"));

        var longs = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        PrimitiveArrayCodec<double>.SwapInPlace(longs);
        Assert.That(Hex(longs), Is.EqualTo("0807060504030201"));
    }

    [Test]
    public void ReadsAcrossSegments()
    {
        var values = Enumerable.Range(0, 1000).ToArray();
        var bytes = TestHelpers.Serialize(values);
        var reader = new MsgPackReader(Segmented(bytes, 7));
        int[]? result = null;
        PrimitiveArrayFormatter<int>.Instance.Deserialize(ref reader, ref result);
        Assert.That(result, Is.EqualTo(values));
    }
}
