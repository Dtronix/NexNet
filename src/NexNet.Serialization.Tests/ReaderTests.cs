using System.Buffers;
using MessagePack;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

[TestFixture]
public class ReaderTests
{
    private delegate void ReferenceOp(ref MessagePackWriter writer);

    private static byte[] Ref(ReferenceOp op)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        op(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    [Test]
    public void ReadsEveryIntegerForm()
    {
        // 5 encoded in every form MessagePack allows.
        var forms = new[]
        {
            Ref((ref MessagePackWriter w) => w.Write(5)),
            Ref((ref MessagePackWriter w) => w.WriteUInt8(5)),
            Ref((ref MessagePackWriter w) => w.WriteUInt16(5)),
            Ref((ref MessagePackWriter w) => w.WriteUInt32(5)),
            Ref((ref MessagePackWriter w) => w.WriteUInt64(5)),
            Ref((ref MessagePackWriter w) => w.WriteInt8(5)),
            Ref((ref MessagePackWriter w) => w.WriteInt16(5)),
            Ref((ref MessagePackWriter w) => w.WriteInt32(5)),
            Ref((ref MessagePackWriter w) => w.WriteInt64(5)),
        };

        foreach (var form in forms)
        {
            Assert.That(Read(form, (ref MsgPackReader r) => r.ReadInt32()), Is.EqualTo(5), Hex(form));
            Assert.That(Read(form, (ref MsgPackReader r) => r.ReadInt64()), Is.EqualTo(5L), Hex(form));
            Assert.That(Read(form, (ref MsgPackReader r) => r.ReadUInt64()), Is.EqualTo(5UL), Hex(form));
            Assert.That(Read(form, (ref MsgPackReader r) => r.ReadByte()), Is.EqualTo((byte)5), Hex(form));
            Assert.That(Read(form, (ref MsgPackReader r) => r.ReadUInt16()), Is.EqualTo((ushort)5), Hex(form));
        }
    }

    [Test]
    public void ReadsNegativeForms()
    {
        var forms = new[]
        {
            Ref((ref MessagePackWriter w) => w.Write(-5)),
            Ref((ref MessagePackWriter w) => w.WriteInt8(-5)),
            Ref((ref MessagePackWriter w) => w.WriteInt16(-5)),
            Ref((ref MessagePackWriter w) => w.WriteInt32(-5)),
            Ref((ref MessagePackWriter w) => w.WriteInt64(-5)),
        };

        foreach (var form in forms)
        {
            Assert.That(Read(form, (ref MsgPackReader r) => r.ReadInt32()), Is.EqualTo(-5), Hex(form));
            Assert.That(Read(form, (ref MsgPackReader r) => r.ReadSByte()), Is.EqualTo((sbyte)-5), Hex(form));
        }
    }

    [Test]
    public void IntegerOverflowThrows()
    {
        var big = Write((ref MsgPackWriter w) => w.Write(300));
        Assert.Throws<NexusSerializationException>(() => Read(big, (ref MsgPackReader r) => r.ReadByte()));

        var negative = Write((ref MsgPackWriter w) => w.Write(-1));
        Assert.Throws<NexusSerializationException>(() => Read(negative, (ref MsgPackReader r) => r.ReadUInt32()));

        var huge = Write((ref MsgPackWriter w) => w.Write(ulong.MaxValue));
        Assert.Throws<NexusSerializationException>(() => Read(huge, (ref MsgPackReader r) => r.ReadInt64()));
    }

    [Test]
    public void FloatsAcceptOtherNumericForms()
    {
        var f32 = Write((ref MsgPackWriter w) => w.Write(1.5f));
        Assert.That(Read(f32, (ref MsgPackReader r) => r.ReadDouble()), Is.EqualTo(1.5));
        var f64 = Write((ref MsgPackWriter w) => w.Write(2.5d));
        Assert.That(Read(f64, (ref MsgPackReader r) => r.ReadSingle()), Is.EqualTo(2.5f));
        var i = Write((ref MsgPackWriter w) => w.Write(7));
        Assert.That(Read(i, (ref MsgPackReader r) => r.ReadDouble()), Is.EqualTo(7d));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(7)]
    public void ReadsAcrossSegments(int segmentSize)
    {
        var data = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write(int.MinValue);
            w.Write(long.MaxValue);
            w.Write("a string that crosses several segments 字");
            w.Write(3.14159);
        });

        var reader = new MsgPackReader(Segmented(data, segmentSize));
        Assert.That(reader.ReadArrayHeader(), Is.EqualTo(4));
        Assert.That(reader.ReadInt32(), Is.EqualTo(int.MinValue));
        Assert.That(reader.ReadInt64(), Is.EqualTo(long.MaxValue));
        Assert.That(reader.ReadString(), Is.EqualTo("a string that crosses several segments 字"));
        Assert.That(reader.ReadDouble(), Is.EqualTo(3.14159));
        Assert.That(reader.End, Is.True);
    }

    [Test]
    public void StrictUtf8RejectsInvalidBytes()
    {
        // fixstr of length 2 with an invalid UTF-8 sequence.
        var data = new byte[] { 0xa2, 0xc3, 0x28 };
        Assert.Throws<NexusSerializationException>(() => Read(data, (ref MsgPackReader r) => r.ReadString()));
        Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted), Is.EqualTo("�("));
    }

    [Test]
    public void ArrayHeaderLargerThanRemainingIsRejectedBeforeAllocation()
    {
        // array32 claiming 4 billion elements with no data.
        var data = new byte[] { 0xdd, 0xff, 0xff, 0xff, 0xff };
        Assert.Throws<NexusSerializationException>(() => Read(data, (ref MsgPackReader r) => r.ReadArrayHeader()));

        var map = new byte[] { 0xdf, 0x00, 0x00, 0x00, 0x02, 0x01, 0x02, 0x03 };
        Assert.Throws<NexusSerializationException>(() => Read(map, (ref MsgPackReader r) => r.ReadMapHeader()));
    }

    [Test]
    public void BinaryLengthLargerThanRemainingIsRejected()
    {
        var data = new byte[] { 0xc6, 0x7f, 0xff, 0xff, 0xff, 0x00 };
        Assert.Throws<NexusSerializationException>(() => Read(data, (ref MsgPackReader r) => r.ReadBinaryToArray()));
    }

    [Test]
    public void DepthLimitIsEnforced()
    {
        // 100 nested arrays of one element, then nil.
        var data = new byte[101];
        for (var i = 0; i < 100; i++)
            data[i] = 0x91;
        data[100] = 0xc0;

        var options = NexusSerializerOptions.Untrusted with { MaxDepth = 10 };
        Assert.Throws<NexusSerializationException>(() =>
        {
            var reader = new MsgPackReader(data, options);
            object? value = null;
            new NestedFormatter().Deserialize(ref reader, ref value);
        });

        var ok = new MsgPackReader(data, NexusSerializerOptions.Untrusted with { MaxDepth = 200 });
        object? okValue = null;
        new NestedFormatter().Deserialize(ref ok, ref okValue);
        Assert.That(ok.End, Is.True);
    }

    private sealed class NestedFormatter : NexusFormatter<object>
    {
        public override void Serialize(ref MsgPackWriter writer, object? value) => throw new NotSupportedException();

        public override void Deserialize(ref MsgPackReader reader, ref object? value)
        {
            if (reader.TryReadNil())
                return;

            var count = reader.ReadArrayHeader();
            reader.Enter();
            for (var i = 0; i < count; i++)
                Deserialize(ref reader, ref value);
            reader.Exit();
        }
    }

    [Test]
    public void NeverUsedCodeThrows()
    {
        var data = new byte[] { 0xc1 };
        Assert.Throws<NexusSerializationException>(() => Read(data, (ref MsgPackReader r) => r.TrySkip()));
    }

    [Test]
    public void ReadRawValueReturnsExactlyOneValue()
    {
        var data = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write("x");
            w.WriteMapHeader(1);
            w.Write(1);
            w.Write(2);
            w.Write(99);
        });

        var reader = new MsgPackReader(data);
        var raw = reader.ReadRawValue();
        Assert.That(raw.Length, Is.EqualTo(data.Length - 1));
        Assert.That(reader.ReadInt32(), Is.EqualTo(99));
    }

    [Test]
    public void TrySkipReturnsFalseOnTruncatedData()
    {
        var data = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write("hello world");
            w.WriteBinary(new byte[300]);
            w.WriteExtHeader(5, 20);
            w.WriteRaw(new byte[20]);
        });

        for (var cut = 0; cut < data.Length; cut++)
        {
            var reader = new MsgPackReader(data.AsMemory(0, cut));
            Assert.That(reader.TrySkip(), Is.False, $"cut {cut}");
            Assert.That(reader.Consumed, Is.EqualTo(0));
        }

        var full = new MsgPackReader(data);
        Assert.That(full.TrySkip(), Is.True);
        Assert.That(full.End, Is.True);
    }

    [Test]
    public void TrySkipHandlesDeepNestingWithoutStack()
    {
        var depth = 1_000_000;
        var data = new byte[depth + 1];
        Array.Fill(data, (byte)0x91, 0, depth);
        data[depth] = 0xc0;
        var reader = new MsgPackReader(data);
        Assert.That(reader.TrySkip(), Is.True);
        Assert.That(reader.End, Is.True);
    }

    [Test]
    public void TrySkipMatchesReferenceOnValidData()
    {
        var random = new Random(1234);
        for (var i = 0; i < 200; i++)
        {
            var value = RandomObject(random, 0);
            var bytes = MessagePackSerializer.Serialize<object?>(value);
            var reader = new MsgPackReader(bytes);
            Assert.That(reader.TrySkip(), Is.True);
            Assert.That(reader.End, Is.True);
        }
    }

    [Test]
    public void TrySkipNeverOverrunsOnRandomBytes()
    {
        var random = new Random(42);
        for (var i = 0; i < 20000; i++)
        {
            var data = new byte[random.Next(0, 64)];
            random.NextBytes(data);
            var reader = new MsgPackReader(data);
            try
            {
                if (reader.TrySkip())
                    Assert.That(reader.Consumed, Is.LessThanOrEqualTo(data.Length));
                else
                    Assert.That(reader.Consumed, Is.EqualTo(0));
            }
            catch (NexusSerializationException)
            {
                // Malformed input is expected to throw this exception only.
            }
        }
    }

    internal static object? RandomObject(Random random, int depth)
    {
        var kind = random.Next(depth > 3 ? 7 : 9);
        return kind switch
        {
            0 => null,
            1 => random.Next() % 2 == 0,
            2 => (long)random.Next(int.MinValue, int.MaxValue) * random.Next(1, 3),
            3 => random.NextDouble(),
            4 => new string('x', random.Next(0, 300)),
            5 => Enumerable.Range(0, random.Next(0, 300)).Select(x => (byte)x).ToArray(),
            6 => (ulong)random.NextInt64(),
            7 => Enumerable.Range(0, random.Next(0, 20)).Select(_ => RandomObject(random, depth + 1)).ToArray(),
            _ => Enumerable.Range(0, random.Next(0, 10)).ToDictionary(x => (object)("k" + x), _ => RandomObject(random, depth + 1)),
        };
    }
}
