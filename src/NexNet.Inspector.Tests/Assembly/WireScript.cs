using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Assembly;

internal delegate void BodyWriter(ref MsgPackWriter writer);

/// <summary>
/// A session written frame by frame in wire order, both directions. NexNet's message classes are internal, so bodies
/// are written with its public MsgPackWriter in the layouts of protocol specification §4.
/// </summary>
internal sealed class WireScript
{
    public List<(TapDirection Direction, byte[] Bytes)> Frames { get; } = [];

    public bool HasPreamble { get; private set; }

    public WireScript Preamble()
    {
        HasPreamble = true;
        Frames.Add((TapDirection.Outbound, [0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1]));
        return this;
    }

    public WireScript Message(TapDirection direction, MessageType type, BodyWriter body)
        => Frame(direction, type, Body(body));

    public WireScript Frame(TapDirection direction, MessageType type, ReadOnlySpan<byte> body)
    {
        var bytes = new byte[3 + body.Length];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), checked((ushort)body.Length));
        body.CopyTo(bytes.AsSpan(3));
        Frames.Add((direction, bytes));
        return this;
    }

    public WireScript Ping(TapDirection direction)
    {
        Frames.Add((direction, [(byte)MessageType.Ping]));
        return this;
    }

    public WireScript PipeWrite(TapDirection direction, ushort pipeId, ReadOnlySpan<byte> data)
        => PostHeader(direction, MessageType.DuplexPipeWrite, [(byte)pipeId, (byte)(pipeId >> 8)], data);

    public WireScript WindowUpdate(TapDirection direction, ushort pipeId, uint credit)
    {
        Span<byte> body = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, credit);
        return PostHeader(direction, MessageType.PipeWindowUpdate, [(byte)pipeId, (byte)(pipeId >> 8)], body);
    }

    public WireScript Chunk(TapDirection direction, ushort invocationId, LargeMessageKind kind, ReadOnlySpan<byte> payload)
        => PostHeader(direction, MessageType.MessageChunk, [(byte)invocationId, (byte)(invocationId >> 8), (byte)kind], payload);

    public WireScript PipeState(TapDirection direction, ushort pipeId, PipeStateFlags state, uint window)
        => Message(direction, MessageType.DuplexPipeUpdateState, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(pipeId);
            w.Write((byte)state);
            w.Write(window);
        });

    public WireScript Invocation(TapDirection direction, ushort id, ushort methodId, InvocationFlags flags, BodyWriter arguments)
        => Frame(direction, MessageType.Invocation, InvocationBody(id, methodId, flags, arguments));

    public WireScript Result(TapDirection direction, ushort id, InvocationResultState state, BodyWriter? value = null)
        => Message(direction, MessageType.InvocationResult, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(value == null ? 2 : 3);
            w.Write(id);
            w.Write((byte)state);
            value?.Invoke(ref w);
        });

    public WireScript Cancellation(TapDirection direction, ushort id)
        => Message(direction, MessageType.InvocationCancellation, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.Write(id);
        });

    public WireScript MessageStart(TapDirection direction, ushort id, LargeMessageKind kind, int totalLength)
        => Message(direction, MessageType.MessageStart, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write(id);
            w.Write((byte)kind);
            w.Write((byte)(kind == LargeMessageKind.Argument ? MessageType.Invocation : MessageType.InvocationResult));
            w.Write((uint)totalLength);
        });

    public WireScript MessageAbort(TapDirection direction, ushort id, LargeMessageKind kind, MessageAbortReason reason)
        => Message(direction, MessageType.MessageAbort, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(3);
            w.Write(id);
            w.Write((byte)kind);
            w.Write((byte)reason);
        });

    public static byte[] InvocationBody(ushort id, ushort methodId, InvocationFlags flags, BodyWriter arguments)
        => Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write(id);
            w.Write(methodId);
            w.Write((byte)flags);
            arguments(ref w);
        });

    public static byte[] Body(BodyWriter body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        body(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A channel item: LEB128 length, then the value.</summary>
    public static byte[] ChannelItem(ReadOnlySpan<byte> value)
    {
        var bytes = new List<byte>();
        var length = (uint)value.Length;
        while (length >= 0x80)
        {
            bytes.Add((byte)(length | 0x80));
            length >>= 7;
        }

        bytes.Add((byte)length);
        bytes.AddRange(value.ToArray());
        return bytes.ToArray();
    }

    private WireScript PostHeader(TapDirection direction, MessageType type, ReadOnlySpan<byte> postHeader, ReadOnlySpan<byte> body)
    {
        var bytes = new byte[3 + postHeader.Length + body.Length];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), checked((ushort)body.Length));
        postHeader.CopyTo(bytes.AsSpan(3));
        body.CopyTo(bytes.AsSpan(3 + postHeader.Length));
        Frames.Add((direction, bytes));
        return this;
    }
}

/// <summary>Feeds a script through two frame parsers into an assembler, one transport op at a time.</summary>
internal static class AssemblyHarness
{
    /// <summary>
    /// Runs the script. With a random source, each run of same-direction frames is cut into ops at random points
    /// (inside headers, bodies and across frames); ops still follow wire order, as a real capture's would, since a
    /// peer cannot answer bytes it has not received.
    /// </summary>
    public static MessageAssembler Run(
        WireScript script,
        Random? random = null,
        IPipeContentResolver? resolver = null,
        Action<MessageAssembler>? beforeComplete = null)
    {
        var inbound = new InMemoryStreamBytes();
        var outbound = new InMemoryStreamBytes();
        var assembler = new MessageAssembler(inbound, outbound) { PipeContentResolver = resolver };
        var outboundParser = new FrameParser(TapDirection.Outbound, expectPreamble: script.HasPreamble);
        var inboundParser = new FrameParser(TapDirection.Inbound, expectPreamble: false);

        long op = 0;
        var i = 0;
        var frames = script.Frames;
        while (i < frames.Count)
        {
            var direction = frames[i].Direction;
            var run = new List<byte>();
            while (i < frames.Count && frames[i].Direction == direction)
                run.AddRange(frames[i++].Bytes);

            var bytes = run.ToArray();
            var position = 0;
            while (position < bytes.Length)
            {
                var size = random == null ? bytes.Length - position : Math.Min(bytes.Length - position, NextSize(random));
                var opBytes = bytes.AsMemory(position, size);
                var store = direction == TapDirection.Inbound ? inbound : outbound;
                var parser = direction == TapDirection.Inbound ? inboundParser : outboundParser;
                store.Append(opBytes);
                parser.Push(op, 1000 + op, opBytes.Span, assembler);
                op++;
                position += size;
            }
        }

        outboundParser.Complete(assembler);
        inboundParser.Complete(assembler);
        beforeComplete?.Invoke(assembler);
        assembler.Complete();
        return assembler;
    }

    private static int NextSize(Random random) => random.Next(3) switch
    {
        0 => random.Next(1, 8),
        1 => random.Next(1, 3000),
        _ => random.Next(1, 120_000),
    };
}

/// <summary>Resolves pipe content by the opener's method ID.</summary>
internal sealed class MethodResolver(Dictionary<ushort, PipeContent> methods) : IPipeContentResolver
{
    public PipeContent Resolve(in InvocationRow opener)
        => methods.TryGetValue(opener.MethodId, out var content) ? content : PipeContent.Raw;
}

/// <summary>Golden vectors by id.</summary>
internal static class GoldenVectors
{
    public static IReadOnlyList<(string Id, byte[] Bytes, JsonElement Value)> Load(string fileName)
    {
        var result = new List<(string, byte[], JsonElement)>();
        foreach (var line in File.ReadAllLines(Path.Combine(RepositoryRoot(), "tests", "golden", fileName)))
        {
            if (line.Length == 0)
                continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            result.Add((root.GetProperty("id").GetString()!, Convert.FromHexString(root.GetProperty("hex").GetString()!),
                root.GetProperty("value").Clone()));
        }

        return result;
    }

    public static byte[] Bytes(string fileName, string id) => Load(fileName).Single(v => v.Id == id).Bytes;

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "NexNet.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (src/NexNet.slnx) not found above " + AppContext.BaseDirectory);
    }
}
