using MessagePack;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

/// <summary>
/// Canonical encoding: NexNet output must equal MessagePack-CSharp output byte-for-byte.
/// </summary>
[TestFixture]
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

    [TestCaseSource(nameof(SignedBoundaries))]
    public void Int64MatchesReference(long value)
    {
        var expected = MessagePackSerializer.Serialize(value);
        var actual = Write((ref MsgPackWriter w) => w.Write(value));
        Assert.That(Hex(actual), Is.EqualTo(Hex(expected)));
    }

    private static IEnumerable<long> Int32Boundaries => SignedBoundaries.Where(v => v is >= int.MinValue and <= int.MaxValue);
    private static IEnumerable<long> Int16Boundaries => SignedBoundaries.Where(v => v is >= short.MinValue and <= short.MaxValue);
    private static IEnumerable<ulong> UInt32Boundaries => UnsignedBoundaries.Where(v => v <= uint.MaxValue);

    [TestCaseSource(nameof(Int32Boundaries))]
    public void Int32MatchesReference(long value)
    {
        var v = (int)value;
        var expected = MessagePackSerializer.Serialize(v);
        var actual = Write((ref MsgPackWriter w) => w.Write(v));
        Assert.That(Hex(actual), Is.EqualTo(Hex(expected)));
    }

    [TestCaseSource(nameof(Int16Boundaries))]
    public void Int16MatchesReference(long value)
    {
        var v = (short)value;
        var expected = MessagePackSerializer.Serialize(v);
        var actual = Write((ref MsgPackWriter w) => w.Write(v));
        Assert.That(Hex(actual), Is.EqualTo(Hex(expected)));
    }

    [TestCaseSource(nameof(UnsignedBoundaries))]
    public void UInt64MatchesReference(ulong value)
    {
        var expected = MessagePackSerializer.Serialize(value);
        var actual = Write((ref MsgPackWriter w) => w.Write(value));
        Assert.That(Hex(actual), Is.EqualTo(Hex(expected)));
    }

    [TestCaseSource(nameof(UInt32Boundaries))]
    public void UInt32MatchesReference(ulong value)
    {
        var v = (uint)value;
        var expected = MessagePackSerializer.Serialize(v);
        var actual = Write((ref MsgPackWriter w) => w.Write(v));
        Assert.That(Hex(actual), Is.EqualTo(Hex(expected)));
    }

    [TestCase((byte)0)]
    [TestCase((byte)127)]
    [TestCase((byte)128)]
    [TestCase((byte)255)]
    public void ByteMatchesReference(byte value)
    {
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(value))));
    }

    [TestCase((sbyte)0)]
    [TestCase((sbyte)-1)]
    [TestCase((sbyte)-32)]
    [TestCase((sbyte)-33)]
    [TestCase(sbyte.MinValue)]
    [TestCase(sbyte.MaxValue)]
    public void SByteMatchesReference(sbyte value)
    {
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(value))));
    }

    [TestCase(0f)]
    [TestCase(1.5f)]
    [TestCase(-3.25f)]
    [TestCase(float.MaxValue)]
    [TestCase(float.NaN)]
    public void SingleMatchesReference(float value)
    {
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(value))));
    }

    [TestCase(0d)]
    [TestCase(1.5d)]
    [TestCase(-3.25d)]
    [TestCase(double.MaxValue)]
    [TestCase(double.Epsilon)]
    public void DoubleMatchesReference(double value)
    {
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(value))));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void BoolMatchesReference(bool value)
    {
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(value))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(value))));
    }

    [Test]
    public void NilMatchesReference()
    {
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write((string?)null))), Is.EqualTo(Hex(MessagePackSerializer.Serialize<string?>(null))));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(9)]
    [TestCase(10)]
    [TestCase(31)]
    [TestCase(32)]
    [TestCase(84)]
    [TestCase(85)]
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(21845)]
    [TestCase(65535)]
    [TestCase(65536)]
    [TestCase(70000)]
    public void AsciiStringMatchesReference(int length)
    {
        var s = new string('a', length);
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(s))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(s))));
    }

    [TestCase("")]
    [TestCase("héllo wörld")]
    [TestCase("日本語のテキスト")]
    [TestCase("emoji 😀😀😀😀😀😀😀😀😀😀😀")]
    [TestCase("mixed ascii and ü and 字 repeated mixed ascii and ü and 字 repeated mixed ascii and ü and 字 repeated")]
    public void UnicodeStringMatchesReference(string s)
    {
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(s))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(s))));
    }

    [Test]
    public void LargeUnicodeStringMatchesReference()
    {
        // Above the large-string threshold (exact byte count path).
        var s = string.Concat(Enumerable.Repeat("字ab", 30000));
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.Write(s))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(s))));
    }

    [TestCase(0)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(65535)]
    [TestCase(65536)]
    public void ArrayHeaderMatchesReference(int count)
    {
        var expected = WriteReference((ref MessagePackWriter w) => w.WriteArrayHeader(count));
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteArrayHeader(count))), Is.EqualTo(Hex(expected)));
    }

    [TestCase(0)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(65536)]
    public void MapHeaderMatchesReference(int count)
    {
        var expected = WriteReference((ref MessagePackWriter w) => w.WriteMapHeader(count));
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteMapHeader(count))), Is.EqualTo(Hex(expected)));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(255)]
    [TestCase(256)]
    [TestCase(65536)]
    public void BinaryMatchesReference(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteBinary(data))), Is.EqualTo(Hex(MessagePackSerializer.Serialize(data))));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(17)]
    [TestCase(256)]
    [TestCase(65536)]
    public void ExtHeaderMatchesReference(int length)
    {
        var expected = WriteReference((ref MessagePackWriter w) => w.WriteExtensionFormatHeader(new ExtensionHeader(42, (uint)length)));
        Assert.That(Hex(Write((ref MsgPackWriter w) => w.WriteExtHeader(42, length))), Is.EqualTo(Hex(expected)));
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
