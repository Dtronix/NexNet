using System.Buffers;
using NexNet.Serialization;

namespace NexNet.Serialization.Tests;

internal delegate void WriteAction(ref MsgPackWriter writer);
internal delegate T ReadFunc<out T>(ref MsgPackReader reader);

internal static class TestHelpers
{
    public static byte[] Write(WriteAction action, bool fixedWidth = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer) { FixedWidth = fixedWidth };
        action(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static T Read<T>(byte[] data, ReadFunc<T> func, NexusSerializerOptions? options = null)
    {
        var reader = new MsgPackReader(data, options);
        var value = func(ref reader);
        Assert.That(reader.End, Is.True, "Reader did not consume all bytes.");
        return value;
    }

    /// <summary>
    /// Splits data into a multi-segment sequence to exercise segment-crossing reads.
    /// </summary>
    public static ReadOnlySequence<byte> Segmented(byte[] data, int segmentSize)
    {
        if (data.Length == 0)
            return ReadOnlySequence<byte>.Empty;

        Segment? first = null;
        Segment? last = null;
        for (var i = 0; i < data.Length; i += segmentSize)
        {
            var chunk = data.AsMemory(i, Math.Min(segmentSize, data.Length - i));
            if (first == null)
            {
                first = last = new Segment(chunk, 0);
            }
            else
            {
                last = last!.Append(chunk);
            }
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    public static byte[] Serialize<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        NexusFormatterRegistry.Get<T>().Serialize(ref writer, value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static T? Deserialize<T>(byte[] data, NexusSerializerOptions? options = null)
    {
        var reader = new MsgPackReader(data, options);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        Assert.That(reader.End, Is.True, "Reader did not consume all bytes.");
        return value;
    }

    public static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
