using MessagePack;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Canonical encoding: NexNet output must equal MessagePack-CSharp output byte-for-byte.
/// </summary>
public class WriterGoldenTests
{
    private static readonly long[] SignedBoundaries =
    [
        0, 1, 31, 32, 127, 128, 255, 256, 32767, 32768, 65535, 65536, int.MaxValue, (long)int.MaxValue + 1, uint.MaxValue,
        (long)uint.MaxValue + 1, long.MaxValue,
        -1, -31, -32, -33, -127, -128, -129, -32767, -32768, -32769, int.MinValue, (long)int.MinValue - 1, long.MinValue
    ];

    private static readonly ulong[] UnsignedBoundaries =
        [0, 1, 127, 128, 255, 256, 65535, 65536, uint.MaxValue, (ulong)uint.MaxValue + 1, ulong.MaxValue];

    [Test]
    [MethodDataSource(nameof(Int64Boundaries))]
    public async Task Int64MatchesReference(long value)
    {
        var expected = MessagePackSerializer.Serialize(value);
        var actual = Write((ref MsgPackWriter w) => w.Write(value));
        await Assert.That(Hex(actual)).IsEqualTo(Hex(expected));
    }

    public static IEnumerable<long> Int64Boundaries() => SignedBoundaries;
    public static IEnumerable<long> Int32Boundaries() => SignedBoundaries.Where(v => v is >= int.MinValue and <= int.MaxValue);
    public static IEnumerable<long> Int16Boundaries() => SignedBoundaries.Where(v => v is >= short.MinValue and <= short.MaxValue);
    public static IEnumerable<ulong> UInt64Boundaries() => UnsignedBoundaries;
    public static IEnumerable<ulong> UInt32Boundaries() => UnsignedBoundaries.Where(v => v <= uint.MaxValue);

    [Test]
    [MethodDataSource(nameof(Int32Boundaries))]
    public async Task Int32MatchesReference(long value)
    {
        var v = (int)value;
        var expected = MessagePackSerializer.Serialize(v);
        var actual = Write((ref MsgPackWriter w) => w.Write(v));
        await Assert.That(Hex(actual)).IsEqualTo(Hex(expected));
    }

    [Test]
    [MethodDataSource(nameof(Int16Boundaries))]
    public async Task Int16MatchesReference(long value)
    {
        var v = (short)value;
        var expected = MessagePackSerializer.Serialize(v);
        var actual = Write((ref MsgPackWriter w) => w.Write(v));
        await Assert.That(Hex(actual)).IsEqualTo(Hex(expected));
    }

    [Test]
    [MethodDataSource(nameof(UInt64Boundaries))]
    public async Task UInt64MatchesReference(ulong value)
    {
        var expected = MessagePackSerializer.Serialize(value);
        var actual = Write((ref MsgPackWriter w) => w.Write(value));
        await Assert.That(Hex(actual)).IsEqualTo(Hex(expected));
    }

    [Test]
    [MethodDataSource(nameof(UInt32Boundaries))]
    public async Task UInt32MatchesReference(ulong value)
    {
        var v = (uint)value;
        var expected = MessagePackSerializer.Serialize(v);
        var actual = Write((ref MsgPackWriter w) => w.Write(v));
        await Assert.That(Hex(actual)).IsEqualTo(Hex(expected));
    }

    [Test]
    [Arguments((byte)0)]
    [Arguments((byte)127)]
    [Arguments((byte)128)]
    [Arguments((byte)255)]
    public async Task ByteMatchesReference(byte value)
    {
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(value)));
    }

    [Test]
    [Arguments((sbyte)0)]
    [Arguments((sbyte)-1)]
    [Arguments((sbyte)-32)]
    [Arguments((sbyte)-33)]
    [Arguments(sbyte.MinValue)]
    [Arguments(sbyte.MaxValue)]
    public async Task SByteMatchesReference(sbyte value)
    {
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(value)));
    }

    [Test]
    [Arguments(0f)]
    [Arguments(1.5f)]
    [Arguments(-3.25f)]
    [Arguments(float.MaxValue)]
    [Arguments(float.NaN)]
    public async Task SingleMatchesReference(float value)
    {
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(value)));
    }

    [Test]
    [Arguments(0d)]
    [Arguments(1.5d)]
    [Arguments(-3.25d)]
    [Arguments(double.MaxValue)]
    [Arguments(double.Epsilon)]
    public async Task DoubleMatchesReference(double value)
    {
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(value)));
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task BoolMatchesReference(bool value)
    {
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(value)));
    }

    [Test]
    public async Task NilMatchesReference()
    {
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write((string?)null)))).IsEqualTo(Hex(MessagePackSerializer.Serialize<string?>(null)));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(31)]
    [Arguments(32)]
    [Arguments(84)]
    [Arguments(85)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(21845)]
    [Arguments(65535)]
    [Arguments(65536)]
    [Arguments(70000)]
    public async Task AsciiStringMatchesReference(int length)
    {
        var s = new string('a', length);
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(s)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(s)));
    }

    [Test]
    [Arguments("")]
    [Arguments("héllo wörld")]
    [Arguments("日本語のテキスト")]
    [Arguments("emoji 😀😀😀😀😀😀😀😀😀😀😀")]
    [Arguments("mixed ascii and ü and 字 repeated mixed ascii and ü and 字 repeated mixed ascii and ü and 字 repeated")]
    public async Task UnicodeStringMatchesReference(string s)
    {
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(s)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(s)));
    }

    [Test]
    public async Task LargeUnicodeStringMatchesReference()
    {
        // Above the large-string threshold (exact byte count path).
        var s = string.Concat(Enumerable.Repeat("字ab", 30000));
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(s)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(s)));
    }

    [Test]
    [Arguments(0)]
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(65535)]
    [Arguments(65536)]
    public async Task ArrayHeaderMatchesReference(int count)
    {
        var expected = WriteReference((ref MessagePackWriter w) => w.WriteArrayHeader(count));
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteArrayHeader(count)))).IsEqualTo(Hex(expected));
    }

    [Test]
    [Arguments(0)]
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(65536)]
    public async Task MapHeaderMatchesReference(int count)
    {
        var expected = WriteReference((ref MessagePackWriter w) => w.WriteMapHeader(count));
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteMapHeader(count)))).IsEqualTo(Hex(expected));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(255)]
    [Arguments(256)]
    [Arguments(65536)]
    public async Task BinaryMatchesReference(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteBinary(data)))).IsEqualTo(Hex(MessagePackSerializer.Serialize(data)));
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(16)]
    [Arguments(17)]
    [Arguments(256)]
    [Arguments(65536)]
    public async Task ExtHeaderMatchesReference(int length)
    {
        var expected = WriteReference((ref MessagePackWriter w) => w.WriteExtensionFormatHeader(new ExtensionHeader(42, (uint)length)));
        await Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteExtHeader(42, length)))).IsEqualTo(Hex(expected));
    }

    internal delegate void ReferenceOp(ref MessagePackWriter writer);

    private static byte[] WriteReference(ReferenceOp op)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        op(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
