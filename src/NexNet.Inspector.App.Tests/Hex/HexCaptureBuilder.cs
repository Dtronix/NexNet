using System.Buffers;
using System.Buffers.Binary;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.App.Tests.Hex;

/// <summary>
/// Builds a <see cref="CaptureSession"/> op by op for the hex view's tests and gallery: frames are written in NexNet's
/// wire layout (protocol specification §1–§3) and fed through the session's own parsers and assembler, on a
/// microsecond clock.
/// </summary>
internal sealed class HexCaptureBuilder
{
    /// <summary>Ticks per second.</summary>
    public const long Frequency = 1_000_000;

    private long _inboundOps;
    private long _outboundOps;

    public HexCaptureBuilder(long inboundStart = 0, long outboundStart = 0, NntlSessionRole role = NntlSessionRole.Server)
    {
        NntlFileHeader header = new(NntlFormat.Version, 0, Frequency, 0, "tests", "1", null);
        NntlSessionOpen open = new(1, role, 0, "Tcp", null, null, null, ReadOnlyMemory<byte>.Empty);
        Session = new CaptureSession(CaptureSource.Live, header, open,
            new LiveOpStore(long.MaxValue, inboundStart), new LiveOpStore(long.MaxValue, outboundStart),
            new SchemaResolution(SchemaStatus.Missing, SchemaSource.None, null, null));
    }

    public CaptureSession Session { get; }

    /// <summary>Adds one transport op at <paramref name="ms"/>.</summary>
    public HexCaptureBuilder Op(TapDirection direction, double ms, byte[] bytes, params NntlByteRange[] redactions)
    {
        long id = direction == TapDirection.Inbound ? ++_inboundOps : ++_outboundOps;
        NntlTransportOp op = new(Session.SessionId, direction, id, (long)(ms * 1000), redactions.Length > 0
            ? NntlTransportOpFlags.Redacted
            : NntlTransportOpFlags.None, redactions, bytes);
        lock (Session.Gate)
            Session.AddOp(op, 0);
        return this;
    }

    /// <summary>Concatenates frames into one op's bytes.</summary>
    public static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Preamble() => [0x4E, 0x6E, 0x50, 0x14, 0, 0, 0, 1];

    public static byte[] Ping() => [(byte)MessageType.Ping];

    public static byte[] Frame(MessageType type, ReadOnlySpan<byte> body)
    {
        byte[] bytes = new byte[3 + body.Length];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), checked((ushort)body.Length));
        body.CopyTo(bytes.AsSpan(3));
        return bytes;
    }

    public static byte[] PipeWrite(ushort pipeId, ReadOnlySpan<byte> data) =>
        PostHeader(MessageType.DuplexPipeWrite, [(byte)pipeId, (byte)(pipeId >> 8)], data);

    public static byte[] Chunk(ushort invocationId, byte kind, ReadOnlySpan<byte> payload) =>
        PostHeader(MessageType.MessageChunk, [(byte)invocationId, (byte)(invocationId >> 8), kind], payload);

    /// <summary>A ClientGreeting (spec §4.1) whose token is <paramref name="tokenLength"/> bytes.</summary>
    public static byte[] ClientGreeting(int tokenLength, out int tokenOffset)
    {
        ArrayBufferWriter<byte> buffer = new();
        MsgPackWriter writer = new(buffer);
        writer.WriteArrayHeader(6);
        writer.Write("v2");
        writer.Write(-1_706_407_620);
        writer.Write(1_628_353_112);
        writer.Flush();
        int header = buffer.WrittenCount;
        writer.WriteBinary(Pattern(tokenLength, 0x5A));
        writer.Write(1_048_576);
        writer.Write(32);
        writer.Flush();
        byte[] body = buffer.WrittenSpan.ToArray();
        // bin 8 header is two bytes for tokens under 256 bytes.
        tokenOffset = 3 + header + (tokenLength < 256 ? 2 : 3);
        return Frame(MessageType.ClientGreeting, body);
    }

    /// <summary>Bytes that read as a recognisable pattern.</summary>
    public static byte[] Pattern(int length, int seed = 0)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)((i * 37) + seed);
        return bytes;
    }

    private static byte[] PostHeader(MessageType type, ReadOnlySpan<byte> postHeader, ReadOnlySpan<byte> body)
    {
        byte[] bytes = new byte[3 + postHeader.Length + body.Length];
        bytes[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), checked((ushort)body.Length));
        postHeader.CopyTo(bytes.AsSpan(3));
        body.CopyTo(bytes.AsSpan(3 + postHeader.Length));
        return bytes;
    }
}
