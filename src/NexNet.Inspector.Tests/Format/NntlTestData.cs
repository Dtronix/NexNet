using System.Buffers.Binary;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Tests.Format;

/// <summary>Builds recordings with a known block sequence and renders blocks as comparable text.</summary>
internal static class NntlTestData
{
    public static readonly NntlFileHeader Header =
        new(NntlFormat.Version, 638_900_000_000_000_000, 10_000_000, 5_000, "inspector-tests", "1.2.3", "test-host");

    public static readonly long[] SessionIds = [0x3A21, 0x7F00_0000_0000_0001, -42];

    public static readonly byte[] SchemaHash = [0x3F, 0x9A, 0x01, 0xC1, 0x4E, 0x54, 0x4C, 0x00];

    public static readonly byte[] SchemaBytes = [0x92, 0xA4, (byte)'C', (byte)'h', (byte)'a', (byte)'t', 0x01];

    /// <summary>A recording holding every block kind for three interleaved sessions.</summary>
    public sealed record Recording(byte[] File, string[] Expected);

    public static Recording Build() => Build(output => output);

    public static Recording Build(Func<Stream, Stream> wrapOutput)
    {
        var expected = new List<string> { Describe(Header) };
        var ms = new MemoryStream();
        using (var writer = new NntlWriter(wrapOutput(ms), Header, leaveOpen: true))
        {
            writer.WriteSchema(SchemaHash, SchemaBytes);
            expected.Add(Describe(new NntlSchema(SchemaHash, SchemaBytes)));

            for (var i = 0; i < SessionIds.Length; i++)
            {
                var open = new NntlSessionOpen(
                    SessionIds[i],
                    i % 2 == 0 ? NntlSessionRole.Server : NntlSessionRole.Client,
                    1_000 + i,
                    i == 2 ? "WebSocket" : "Tcp",
                    i == 0 ? "primary" : null,
                    "10.0.4.12:7711",
                    i == 1 ? null : $"10.0.4.{20 + i}:5{i}000",
                    i == 2 ? ReadOnlyMemory<byte>.Empty : SchemaHash);
                writer.WriteSessionOpen(open);
                expected.Add(Describe(open));
            }

            for (var k = 0; k < 30; k++)
            {
                var sessionId = SessionIds[k % 3];
                var direction = (TapDirection)(k % 2);
                var bytes = OpBytes(k);
                NntlByteRange[] redactions = k == 4 ? [new NntlByteRange(1, 2), new NntlByteRange(5, 3)] : [];
                writer.WriteTransportOp(sessionId, direction, k / 3, 2_000 + k * 10, bytes, redactions);
                expected.Add(Describe(new NntlTransportOp(
                    sessionId, direction, k / 3, 2_000 + k * 10,
                    redactions.Length > 0 ? NntlTransportOpFlags.Redacted : NntlTransportOpFlags.None,
                    redactions, bytes)));

                if (k == 10)
                {
                    var gap = new NntlGap(SessionIds[0], TapDirection.Outbound, 2_101, 2_105, 17, 4_096);
                    writer.WriteGap(gap);
                    expected.Add(Describe(gap));
                }

                if (k == 15)
                {
                    NntlSnapshotSection[] sections =
                    [
                        new(NntlSnapshotSectionKind.OpenPipes, new byte[] { 0x91, 0x03 }),
                        new(NntlSnapshotSectionKind.CollectionState, new byte[] { 0x90 }),
                        new((NntlSnapshotSectionKind)99, new byte[] { 0xC0 }),
                    ];
                    writer.WriteSnapshot(SessionIds[1], 2_155, sections);
                    expected.Add(Describe(new NntlSnapshot(SessionIds[1], 2_155, NntlSnapshot.CurrentVersion, sections)));
                }
            }

            for (var i = 0; i < SessionIds.Length; i++)
            {
                var close = new NntlSessionClose(
                    SessionIds[i],
                    3_000 + i,
                    i == 1 ? NntlCloseKind.RecordingStopped : NntlCloseKind.Disconnected,
                    i == 1 ? DisconnectReason.None : DisconnectReason.Graceful);
                writer.WriteSessionClose(close);
                expected.Add(Describe(close));
            }
        }

        return new Recording(ms.ToArray(), expected.ToArray());
    }

    public static byte[] OpBytes(int k)
    {
        var bytes = new byte[(k * 7 % 50) + 9];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(k * 31 + i);
        // Put a sync marker inside some payloads so resync has to reject false candidates.
        if (k % 4 == 0)
            NntlFormat.SyncMarker.CopyTo(bytes.AsSpan(2));
        return bytes;
    }

    public static long IndexOffset(byte[] file) =>
        BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(file.Length - NntlFormat.TrailerSize));

    /// <summary>Every indexed block of the reader, read by offset and rendered as text, in file order.</summary>
    public static string[] ReadAll(NntlReader reader)
    {
        var refs = reader.Index.ToFileOrder();
        var result = new string[refs.Length];
        for (var i = 0; i < refs.Length; i++)
            result[i] = Describe(reader.ReadBlock(refs[i].Offset));
        return result;
    }

    public static string Describe(NntlBlock block) => block.Kind switch
    {
        NntlBlockKind.Header => Describe(NntlCodec.DecodeHeader(block.Body)),
        NntlBlockKind.SessionOpen => Describe(NntlCodec.DecodeSessionOpen(block.Body)),
        NntlBlockKind.Schema => Describe(NntlCodec.DecodeSchema(block.Body)),
        NntlBlockKind.TransportOp => Describe(NntlCodec.DecodeTransportOp(block.Body)),
        NntlBlockKind.Gap => Describe(NntlCodec.DecodeGap(block.Body)),
        NntlBlockKind.Snapshot => Describe(NntlCodec.DecodeSnapshot(block.Body)),
        NntlBlockKind.SessionClose => Describe(NntlCodec.DecodeSessionClose(block.Body)),
        _ => $"? {block.Kind}",
    };

    public static string Describe(NntlFileHeader h) =>
        $"H {h.FormatVersion} {h.WallClockUtcTicks} {h.StopwatchFrequency} {h.StopwatchTicksAtAnchor} {h.Producer} {h.ProducerVersion} {h.Host}";

    public static string Describe(NntlSessionOpen o) =>
        $"O {o.SessionId} {o.Role} {o.Ticks} {o.TransportName} {o.EndpointName ?? "-"} {o.LocalEndpoint ?? "-"} {o.RemoteEndpoint ?? "-"} {Hex(o.SchemaHash)}";

    public static string Describe(NntlSchema s) => $"S {Hex(s.Hash)} {Hex(s.Schema)}";

    public static string Describe(NntlTransportOp t) =>
        $"T {t.SessionId} {t.Direction} {t.OpId} {t.Ticks} {t.Flags} [{string.Join(",", t.Redactions.ToArray())}] {Hex(t.Bytes)}";

    public static string Describe(NntlGap g) =>
        $"G {g.SessionId} {g.Direction} {g.FromTicks} {g.ToTicks} {g.DroppedOps} {g.DroppedBytes}";

    public static string Describe(NntlSnapshot s)
    {
        var sections = s.Sections.ToArray().Select(x => $"{(int)x.Kind}:{Hex(x.Data)}");
        return $"P {s.SessionId} {s.Ticks} {s.Version} [{string.Join(",", sections)}]";
    }

    public static string Describe(NntlSessionClose c) => $"C {c.SessionId} {c.Ticks} {c.Kind} {c.DisconnectReason}";

    private static string Hex(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(bytes.Span);
}

/// <summary>A write-only stream that refuses Seek, Position and Length, like a pipe or socket.</summary>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
}
