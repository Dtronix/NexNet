using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using NexNet.Serialization;
using static NexNet.Inspector.Core.Model.TapDirection;

namespace NexNet.Inspector.Tests.Assembly;

/// <summary>The sessions the assembly tests replay; the tapped endpoint is the client unless noted.</summary>
internal static class Scenarios
{
    public const ushort UploadPipe = 1 | (2 << 8);
    public const ushort ChannelPipe = 2 | (1 << 8);
    public const ushort CollectionPipe = 3 | (1 << 8);
    public const ushort UploadMethod = 4;
    public const ushort AttachmentMethod = 7;
    public const ushort ChannelMethod = 11;
    public const ushort CollectionMethod = 20;

    public static readonly byte[] Token = "secret-token"u8.ToArray();

    /// <summary>SendAttachment: a 5-chunk argument interleaved with an upload pipe's writes and a ping.</summary>
    public static (WireScript Script, byte[] Arguments) SendAttachment()
    {
        var attachment = new byte[300_000];
        new Random(7).NextBytes(attachment);
        var arguments = WireScript.Body((ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.WriteBinary(attachment);
        });
        var inner = WireScript.InvocationBody(5, AttachmentMethod, InvocationFlags.None, (ref MsgPackWriter w) => w.WriteRaw(arguments));
        var chunks = Split(inner, ushort.MaxValue);

        var s = new WireScript().Preamble()
            .Message(Outbound, MessageType.ClientGreeting, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(6);
                w.Write("v1");
                w.Write(300);
                w.Write(-5);
                w.WriteBinary(Token);
                w.Write(4 * 1024 * 1024);
                w.Write(32);
            })
            .Message(Inbound, MessageType.ServerGreeting, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(5);
                w.Write(-5);
                w.Write(42L);
                w.Write(65536);
                w.Write(128);
                w.Write(30000u);
            })
            .Invocation(Outbound, 0, UploadMethod, InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.Write((byte)1);
            })
            .PipeState(Inbound, UploadPipe, PipeStateFlags.Ready, 262144)
            .WindowUpdate(Outbound, UploadPipe, 262144)
            .MessageStart(Outbound, 5, LargeMessageKind.Argument, inner.Length)
            .Chunk(Outbound, 5, LargeMessageKind.Argument, chunks[0])
            .PipeWrite(Outbound, UploadPipe, new byte[100])
            .Chunk(Outbound, 5, LargeMessageKind.Argument, chunks[1])
            .Ping(Outbound)
            .Chunk(Outbound, 5, LargeMessageKind.Argument, chunks[2])
            .PipeWrite(Outbound, UploadPipe, new byte[50])
            .Chunk(Outbound, 5, LargeMessageKind.Argument, chunks[3])
            .Chunk(Outbound, 5, LargeMessageKind.Argument, chunks[4])
            .Ping(Inbound)
            .Result(Inbound, 5, InvocationResultState.CompletedResult, (ref MsgPackWriter w) => w.WriteNil())
            .PipeState(Outbound, UploadPipe, PipeStateFlags.Complete, 0)
            .PipeState(Inbound, UploadPipe, PipeStateFlags.Complete, 0);
        if (chunks.Count != 5)
            throw new InvalidOperationException("The attachment must stream as 5 chunks.");
        return (s, arguments);
    }

    /// <summary>A refused argument stream (Busy, an in-flight chunk, Ack) and a caller-canceled one.</summary>
    public static WireScript Aborts()
    {
        var refused = WireScript.InvocationBody(9, 8, InvocationFlags.None, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.WriteBinary(new byte[200_000]);
        });
        var canceled = WireScript.InvocationBody(10, 8, InvocationFlags.None, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.WriteBinary(new byte[100_000]);
        });
        var refusedChunks = Split(refused, ushort.MaxValue);
        var canceledChunks = Split(canceled, ushort.MaxValue);

        return new WireScript()
            .MessageStart(Outbound, 9, LargeMessageKind.Argument, refused.Length)
            .Chunk(Outbound, 9, LargeMessageKind.Argument, refusedChunks[0])
            .MessageAbort(Inbound, 9, LargeMessageKind.Argument, MessageAbortReason.Busy)
            .Chunk(Outbound, 9, LargeMessageKind.Argument, refusedChunks[1])
            .MessageAbort(Outbound, 9, LargeMessageKind.Argument, MessageAbortReason.Ack)
            .MessageStart(Outbound, 10, LargeMessageKind.Argument, canceled.Length)
            .Chunk(Outbound, 10, LargeMessageKind.Argument, canceledChunks[0])
            .Ping(Outbound)
            .MessageAbort(Outbound, 10, LargeMessageKind.Argument, MessageAbortReason.Cancelled);
    }

    /// <summary>The items written on the channel pipe, outbound then inbound.</summary>
    public static (byte[][] Outbound, byte[][] Inbound) ChannelValues()
    {
        byte[][] outbound =
        [
            WireScript.Body((ref MsgPackWriter w) => w.Write("hello")),
            WireScript.Body((ref MsgPackWriter w) => w.WriteBinary(new byte[100_000])),
            WireScript.Body((ref MsgPackWriter w) => w.Write(42)),
            WireScript.Body((ref MsgPackWriter w) => w.Write(new string('z', 200))),
        ];
        byte[][] inbound =
        [
            WireScript.Body((ref MsgPackWriter w) => w.Write(1)),
            WireScript.Body((ref MsgPackWriter w) => w.Write("ack")),
        ];
        return (outbound, inbound);
    }

    /// <summary>
    /// A channel pipe whose items cross DuplexPipeWrite frames: the first frame ends one byte into the second item's
    /// length prefix, and that item spans four frames.
    /// </summary>
    public static WireScript Channel()
    {
        var (outboundValues, inboundValues) = ChannelValues();
        var stream = outboundValues.SelectMany(v => WireScript.ChannelItem(v)).ToArray();
        var firstItem = WireScript.ChannelItem(outboundValues[0]).Length;
        int[] frameSizes = [firstItem + 1, ushort.MaxValue, 30_000];

        var s = new WireScript()
            .Invocation(Outbound, 0, ChannelMethod, InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(2);
                w.Write(77);
                w.Write((byte)2);
            })
            .PipeState(Inbound, ChannelPipe, PipeStateFlags.Ready, 262144);
        var position = 0;
        foreach (var size in frameSizes)
        {
            s.PipeWrite(Outbound, ChannelPipe, stream.AsSpan(position, size));
            position += size;
        }

        s.Ping(Outbound);
        s.PipeWrite(Outbound, ChannelPipe, stream.AsSpan(position));
        s.PipeWrite(Inbound, ChannelPipe, inboundValues.SelectMany(v => WireScript.ChannelItem(v)).ToArray());
        return s;
    }

    /// <summary>Cancellation and every result state, with an inbound call reusing an outbound call's ID.</summary>
    public static WireScript Cancellation()
    {
        BodyWriter none = (ref MsgPackWriter w) => w.WriteArrayHeader(0);
        return new WireScript()
            .Invocation(Outbound, 4, 3, InvocationFlags.None, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.Write(5);
            })
            .Cancellation(Outbound, 4)
            .Invocation(Outbound, 6, 3, InvocationFlags.None, none)
            .Invocation(Outbound, 7, 3, InvocationFlags.None, none)
            .Invocation(Inbound, 4, 9, InvocationFlags.None, none)
            .Result(Inbound, 4, InvocationResultState.Canceled)
            .Result(Inbound, 6, InvocationResultState.Exception, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(3);
                w.Write(500);
                w.Write("Foo");
                w.Write("bar");
            })
            .Result(Inbound, 7, InvocationResultState.Unauthorized)
            .Result(Outbound, 4, InvocationResultState.CompletedResult, (ref MsgPackWriter w) => w.Write(42))
            .Cancellation(Inbound, 99);
    }

    /// <summary>
    /// The tapped endpoint is the server: the client connects a collection, the server sends every golden collection
    /// item, and the client sends one more.
    /// </summary>
    public static (WireScript Script, IReadOnlyList<(string Id, byte[] Bytes, System.Text.Json.JsonElement Value)> Vectors) Collection()
    {
        var vectors = GoldenVectors.Load("collections.jsonl");
        var serverItems = vectors.SelectMany(v => WireScript.ChannelItem(v.Bytes)).ToArray();
        var s = new WireScript()
            .Frame(Inbound, MessageType.ClientGreeting, GoldenVectors.Bytes("messages.jsonl", "msg.client-greeting"))
            .Frame(Outbound, MessageType.ServerGreeting, GoldenVectors.Bytes("messages.jsonl", "msg.server-greeting"))
            .Invocation(Inbound, 0, CollectionMethod, InvocationFlags.IgnoreReturn | InvocationFlags.DuplexPipe, (ref MsgPackWriter w) =>
            {
                w.WriteArrayHeader(1);
                w.Write((byte)3);
            })
            .PipeState(Outbound, CollectionPipe, PipeStateFlags.Ready, 262144)
            .WindowUpdate(Inbound, CollectionPipe, 262144)
            .PipeWrite(Outbound, CollectionPipe, serverItems.AsSpan(0, 50))
            .PipeWrite(Outbound, CollectionPipe, serverItems.AsSpan(50))
            .PipeWrite(Inbound, CollectionPipe, WireScript.ChannelItem(GoldenVectors.Bytes("collections.jsonl", "collection.insert")));
        return (s, vectors);
    }

    public static List<byte[]> Split(byte[] bytes, int size)
    {
        var result = new List<byte[]>();
        for (var i = 0; i < bytes.Length; i += size)
            result.Add(bytes.AsSpan(i, Math.Min(size, bytes.Length - i)).ToArray());
        return result;
    }
}
