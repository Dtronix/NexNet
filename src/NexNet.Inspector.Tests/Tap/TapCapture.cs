using System.Buffers;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Tap;
using NexNet.Serialization;

namespace NexNet.Inspector.Tests.Tap;

/// <summary>One block of a captured stream, with its body copied.</summary>
internal readonly record struct CapturedBlock(NntlBlockKind Kind, byte[] Body);

/// <summary>Everything a viewer read from a tap stream.</summary>
internal sealed class TapCapture
{
    public List<CapturedBlock> Blocks { get; } = [];

    public bool Complete { get; set; }

    public IEnumerable<NntlTransportOp> Ops(long sessionId, TapDirection direction) => Blocks
        .Where(b => b.Kind == NntlBlockKind.TransportOp)
        .Select(b => NntlCodec.DecodeTransportOp(b.Body))
        .Where(o => o.SessionId == sessionId && o.Direction == direction);

    public IEnumerable<NntlGap> Gaps(long sessionId, TapDirection direction) => Blocks
        .Where(b => b.Kind == NntlBlockKind.Gap)
        .Select(b => NntlCodec.DecodeGap(b.Body))
        .Where(g => g.SessionId == sessionId && g.Direction == direction);

    public static async Task<TapCapture> ReadAllAsync(InspectorStream stream)
    {
        var capture = new TapCapture();
        while (await stream.ReadAsync())
        {
            if (stream.Current.Kind != NntlBlockKind.Index)
                capture.Blocks.Add(new CapturedBlock(stream.Current.Kind, stream.Current.Body.ToArray()));
        }
        capture.Complete = stream.IsComplete;
        return capture;
    }

    /// <summary>Reads every block of a recording, in file order.</summary>
    public static TapCapture FromRecording(byte[] file, out NntlReader reader)
    {
        reader = NntlReader.Open(new MemoryStream(file));
        var capture = new TapCapture { Complete = reader.IndexSource == NntlIndexSource.Footer };
        foreach (var entry in reader.Index.ToFileOrder())
        {
            var block = reader.ReadBlock(entry.Offset);
            if (block.Kind != NntlBlockKind.Index)
                capture.Blocks.Add(new CapturedBlock(block.Kind, block.Body.ToArray()));
        }

        return capture;
    }
}

/// <summary>Frames and assembles one session's captured ops with the I02 parser and the I03 assembler.</summary>
internal sealed class AssembledSession
{
    public AssembledSession(IEnumerable<NntlTransportOp> ops, NntlSessionRole role, bool fromInception)
    {
        Inbound = new InMemoryStreamBytes();
        Outbound = new InMemoryStreamBytes();
        Assembler = new MessageAssembler(Inbound, Outbound, role);
        InboundParser = new FrameParser(TapDirection.Inbound, fromInception);
        OutboundParser = new FrameParser(TapDirection.Outbound, fromInception);
        foreach (var op in ops)
        {
            var bytes = op.Bytes.ToArray();
            if (op.Direction == TapDirection.Inbound)
            {
                Inbound.Append(bytes);
                InboundParser.Push(op.OpId, op.Ticks, bytes, Assembler);
            }
            else
            {
                Outbound.Append(bytes);
                OutboundParser.Push(op.OpId, op.Ticks, bytes, Assembler);
            }
        }
    }

    public InMemoryStreamBytes Inbound { get; }
    public InMemoryStreamBytes Outbound { get; }
    public MessageAssembler Assembler { get; }
    public FrameParser InboundParser { get; }
    public FrameParser OutboundParser { get; }
}

internal static class TapTestData
{
    public static readonly byte[] Preamble = [0x4E, 0x6E, 0x50, 0x14, 0x00, 0x00, 0x00, 0x01];

    /// <summary>The preamble followed by a ClientGreeting frame carrying <paramref name="token"/>.</summary>
    public static byte[] ClientGreeting(byte[]? token)
    {
        var body = new ArrayBufferWriter<byte>();
        var w = new MsgPackWriter(body);
        w.WriteArrayHeader(6);
        w.WriteNil();
        w.Write(123);
        w.Write(456);
        if (token == null)
            w.WriteNil();
        else
            w.WriteBinary(token);
        w.Write(65535);
        w.Write(16);
        w.Flush();

        var frame = new byte[Preamble.Length + 3 + body.WrittenCount];
        Preamble.CopyTo(frame, 0);
        frame[8] = (byte)Messages.MessageType.ClientGreeting;
        frame[9] = (byte)body.WrittenCount;
        frame[10] = (byte)(body.WrittenCount >> 8);
        body.WrittenSpan.CopyTo(frame.AsSpan(11));
        return frame;
    }

    public static byte[] Marker(byte[] token)
    {
        var marker = new byte[token.Length];
        InspectorRedaction.WriteMarker(token, marker);
        return marker;
    }

    public static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    public static ulong SchemaHash<T>() where T : Diagnostics.IInspectorSchemaSource => T.InspectorSchemaHash;
}

/// <summary>A memory stream that can be copied while a recorder writes to it, like a file read after a crash.</summary>
internal sealed class SnapshotStream : Stream
{
    private readonly Lock _gate = new();
    private readonly MemoryStream _inner = new();

    public byte[] Copy()
    {
        lock (_gate)
            return _inner.ToArray();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        lock (_gate)
            _inner.Write(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
