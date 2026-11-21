using System.Buffers;
using MessagePack;
using static NexNet.Serialization.Tests.TestHelpers;

namespace NexNet.Serialization.Tests;

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
    public async Task ReadsEveryIntegerForm()
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
            await Assert.That(Read(form, (ref MsgPackReader r) => r.ReadInt32())).IsEqualTo(5).Because(Hex(form));
            await Assert.That(Read(form, (ref MsgPackReader r) => r.ReadInt64())).IsEqualTo(5L).Because(Hex(form));
            await Assert.That(Read(form, (ref MsgPackReader r) => r.ReadUInt64())).IsEqualTo(5UL).Because(Hex(form));
            await Assert.That(Read(form, (ref MsgPackReader r) => r.ReadByte())).IsEqualTo((byte)5).Because(Hex(form));
            await Assert.That(Read(form, (ref MsgPackReader r) => r.ReadUInt16())).IsEqualTo((ushort)5).Because(Hex(form));
        }
    }

    [Test]
    public async Task ReadsNegativeForms()
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
            await Assert.That(Read(form, (ref MsgPackReader r) => r.ReadInt32())).IsEqualTo(-5).Because(Hex(form));
            await Assert.That(Read(form, (ref MsgPackReader r) => r.ReadSByte())).IsEqualTo((sbyte)-5).Because(Hex(form));
        }
    }

    [Test]
    public async Task IntegerOverflowThrows()
    {
        var big = Write((ref MsgPackWriter w) => w.Write(300));
        await Assert.That(() => Read(big, (ref MsgPackReader r) => r.ReadByte())).ThrowsExactly<NexusSerializationException>();

        var negative = Write((ref MsgPackWriter w) => w.Write(-1));
        await Assert.That(() => Read(negative, (ref MsgPackReader r) => r.ReadUInt32())).ThrowsExactly<NexusSerializationException>();

        var huge = Write((ref MsgPackWriter w) => w.Write(ulong.MaxValue));
        await Assert.That(() => Read(huge, (ref MsgPackReader r) => r.ReadInt64())).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task FloatsAcceptOtherNumericForms()
    {
        var f32 = Write((ref MsgPackWriter w) => w.Write(1.5f));
        await Assert.That(Read(f32, (ref MsgPackReader r) => r.ReadDouble())).IsEqualTo(1.5);
        var f64 = Write((ref MsgPackWriter w) => w.Write(2.5d));
        await Assert.That(Read(f64, (ref MsgPackReader r) => r.ReadSingle())).IsEqualTo(2.5f);
        var i = Write((ref MsgPackWriter w) => w.Write(7));
        await Assert.That(Read(i, (ref MsgPackReader r) => r.ReadDouble())).IsEqualTo(7d);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(7)]
    public async Task ReadsAcrossSegments(int segmentSize)
    {
        var data = Write((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write(int.MinValue);
            w.Write(long.MaxValue);
            w.Write("a string that crosses several segments 字");
            w.Write(3.14159);
        });

        // The reader is a ref struct and cannot live across an await: read everything first, then assert.
        int header, int32;
        long int64;
        string? text;
        double number;
        bool end;
        {
            var reader = new MsgPackReader(Segmented(data, segmentSize));
            header = reader.ReadArrayHeader();
            int32 = reader.ReadInt32();
            int64 = reader.ReadInt64();
            text = reader.ReadString();
            number = reader.ReadDouble();
            end = reader.End;
        }

        await Assert.That(header).IsEqualTo(4);
        await Assert.That(int32).IsEqualTo(int.MinValue);
        await Assert.That(int64).IsEqualTo(long.MaxValue);
        await Assert.That(text).IsEqualTo("a string that crosses several segments 字");
        await Assert.That(number).IsEqualTo(3.14159);
        await Assert.That(end).IsTrue();
    }

    [Test]
    public async Task StrictUtf8RejectsInvalidBytes()
    {
        // fixstr of length 2 with an invalid UTF-8 sequence.
        var data = new byte[] { 0xa2, 0xc3, 0x28 };
        await Assert.That(() => Read(data, (ref MsgPackReader r) => r.ReadString())).ThrowsExactly<NexusSerializationException>();
        await Assert.That(Read(data, (ref MsgPackReader r) => r.ReadString(), NexusSerializerOptions.Trusted)).IsEqualTo("�(");
    }

    [Test]
    public async Task ArrayHeaderLargerThanRemainingIsRejectedBeforeAllocation()
    {
        // array32 claiming 4 billion elements with no data.
        var data = new byte[] { 0xdd, 0xff, 0xff, 0xff, 0xff };
        await Assert.That(() => Read(data, (ref MsgPackReader r) => r.ReadArrayHeader())).ThrowsExactly<NexusSerializationException>();

        var map = new byte[] { 0xdf, 0x00, 0x00, 0x00, 0x02, 0x01, 0x02, 0x03 };
        await Assert.That(() => Read(map, (ref MsgPackReader r) => r.ReadMapHeader())).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task BinaryLengthLargerThanRemainingIsRejected()
    {
        var data = new byte[] { 0xc6, 0x7f, 0xff, 0xff, 0xff, 0x00 };
        await Assert.That(() => Read(data, (ref MsgPackReader r) => r.ReadBinaryToArray())).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task DepthLimitIsEnforced()
    {
        // 100 nested arrays of one element, then nil.
        var data = new byte[101];
        for (var i = 0; i < 100; i++)
            data[i] = 0x91;
        data[100] = 0xc0;

        var options = NexusSerializerOptions.Untrusted with { MaxDepth = 10 };
        await Assert.That(() =>
        {
            var reader = new MsgPackReader(data, options);
            object? value = null;
            new NestedFormatter().Deserialize(ref reader, ref value);
        }).ThrowsExactly<NexusSerializationException>();

        var ok = new MsgPackReader(data, NexusSerializerOptions.Untrusted with { MaxDepth = 200 });
        object? okValue = null;
        new NestedFormatter().Deserialize(ref ok, ref okValue);
        await Assert.That(ok.End).IsTrue();
    }

    [Test]
    public async Task ElementLimitCountsCumulativelyAcrossNestedContainers()
    {
        // Three arrays of four nils: 3 + 12 = 15 elements, each array within a limit of 10 on its own.
        var data = new byte[] { 0x93, 0x94, 0xc0, 0xc0, 0xc0, 0xc0, 0x94, 0xc0, 0xc0, 0xc0, 0xc0, 0x94, 0xc0, 0xc0, 0xc0, 0xc0 };

        await Assert.That(() => ReadNested(data, NexusSerializerOptions.Untrusted with { MaxElements = 10 })).ThrowsExactly<NexusSerializationException>();
        await Assert.That(() => ReadNested(data, NexusSerializerOptions.Untrusted with { MaxElements = 15 })).ThrowsNothing();

        // 0, the default, means no limit.
        await Assert.That(() => ReadNested(data, NexusSerializerOptions.Untrusted)).ThrowsNothing();

        static void ReadNested(byte[] data, NexusSerializerOptions options)
        {
            var reader = new MsgPackReader(data, options);
            var outer = reader.ReadArrayHeader();
            for (var i = 0; i < outer; i++)
            {
                var inner = reader.ReadArrayHeader();
                for (var j = 0; j < inner; j++)
                    reader.ReadNil();
            }
        }
    }

    [Test]
    public async Task ElementLimitCountsMapEntriesAsKeyAndValue()
    {
        // A map of three pairs is six elements.
        var data = new byte[] { 0x83, 0x01, 0xc0, 0x02, 0xc0, 0x03, 0xc0 };

        await Assert.That(() =>
        {
            var reader = new MsgPackReader(data, NexusSerializerOptions.Untrusted with { MaxElements = 5 });
            reader.ReadMapHeader();
        }).ThrowsExactly<NexusSerializationException>();
        var ok = new MsgPackReader(data, NexusSerializerOptions.Untrusted with { MaxElements = 6 });
        await Assert.That(ok.ReadMapHeader()).IsEqualTo(3);
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
    public async Task NeverUsedCodeThrows()
    {
        var data = new byte[] { 0xc1 };
        await Assert.That(() => Read(data, (ref MsgPackReader r) => r.TrySkip())).ThrowsExactly<NexusSerializationException>();
    }

    [Test]
    public async Task ReadRawValueReturnsExactlyOneValue()
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

        long rawLength;
        int next;
        {
            var reader = new MsgPackReader(data);
            rawLength = reader.ReadRawValue().Length;
            next = reader.ReadInt32();
        }

        await Assert.That(rawLength).IsEqualTo(data.Length - 1);
        await Assert.That(next).IsEqualTo(99);
    }

    [Test]
    public async Task TrySkipReturnsFalseOnTruncatedData()
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
            var (skipped, _, consumed) = TrySkip(data.AsMemory(0, cut));
            await Assert.That(skipped).IsFalse().Because($"cut {cut}");
            await Assert.That(consumed).IsEqualTo(0);
        }

        var full = TrySkip(data);
        await Assert.That(full.Skipped).IsTrue();
        await Assert.That(full.End).IsTrue();
    }

    [Test]
    public async Task TrySkipHandlesDeepNestingWithoutStack()
    {
        var depth = 1_000_000;
        var data = new byte[depth + 1];
        Array.Fill(data, (byte)0x91, 0, depth);
        data[depth] = 0xc0;
        var (skipped, end, _) = TrySkip(data);
        await Assert.That(skipped).IsTrue();
        await Assert.That(end).IsTrue();
    }

    [Test]
    public async Task TrySkipMatchesReferenceOnValidData()
    {
        var random = new Random(1234);
        for (var i = 0; i < 200; i++)
        {
            var value = RandomObject(random, 0);
            var bytes = MessagePackSerializer.Serialize<object?>(value);
            var (skipped, end, _) = TrySkip(bytes);
            await Assert.That(skipped).IsTrue();
            await Assert.That(end).IsTrue();
        }
    }

    [Test]
    public async Task TrySkipNeverOverrunsOnRandomBytes()
    {
        var random = new Random(42);
        for (var i = 0; i < 20000; i++)
        {
            var data = new byte[random.Next(0, 64)];
            random.NextBytes(data);
            bool skipped;
            long consumed;
            try
            {
                (skipped, _, consumed) = TrySkip(data);
            }
            catch (NexusSerializationException)
            {
                // Malformed input is expected to throw this exception only.
                continue;
            }

            if (skipped)
                await Assert.That(consumed).IsLessThanOrEqualTo(data.Length);
            else
                await Assert.That(consumed).IsEqualTo(0);
        }
    }

    /// <summary>
    /// Runs <see cref="MsgPackReader.TrySkip"/> on a fresh reader and returns what the assertions need, so the
    /// ref struct reader is never live across an await.
    /// </summary>
    private static (bool Skipped, bool End, long Consumed) TrySkip(ReadOnlyMemory<byte> data)
    {
        var reader = new MsgPackReader(data);
        var skipped = reader.TrySkip();
        return (skipped, reader.End, reader.Consumed);
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
