using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Format;

/// <summary>Encodes and decodes block bodies. Each body is one MessagePack array.</summary>
/// <remarks>
/// Versioning: a later minor revision may append elements to a body array. Decoders require the elements this
/// version defines and skip any that follow, so older readers keep working.
/// </remarks>
public static class NntlCodec
{
    private const int HeaderFields = 8;
    private const int SessionOpenFields = 8;
    private const int SchemaFields = 2;
    private const int TransportOpFields = 7;
    private const int GapFields = 6;
    private const int SnapshotFields = 4;
    private const int SessionCloseFields = 4;
    private const int IndexFields = 2;
    private const int IndexVersion = 1;

    // ---------------------------------------------------------------- Encode

    internal static void WriteHeader(ref MsgPackWriter w, in NntlFileHeader h)
    {
        w.WriteArrayHeader(HeaderFields);
        w.Write(NntlFormat.Magic);
        w.Write(h.FormatVersion);
        w.Write(h.WallClockUtcTicks);
        w.Write(h.StopwatchFrequency);
        w.Write(h.StopwatchTicksAtAnchor);
        w.Write(h.Producer);
        w.Write(h.ProducerVersion);
        w.Write(h.Host);
    }

    internal static void WriteSessionOpen(ref MsgPackWriter w, in NntlSessionOpen s)
    {
        w.WriteArrayHeader(SessionOpenFields);
        w.Write(s.SessionId);
        w.Write((byte)s.Role);
        w.Write(s.Ticks);
        w.Write(s.TransportName);
        w.Write(s.EndpointName);
        w.Write(s.LocalEndpoint);
        w.Write(s.RemoteEndpoint);
        if (s.SchemaHash.IsEmpty)
            w.WriteNil();
        else
            w.WriteBinary(s.SchemaHash.Span);
    }

    internal static void WriteSchema(ref MsgPackWriter w, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> schema)
    {
        w.WriteArrayHeader(SchemaFields);
        w.WriteBinary(hash);
        w.WriteBinary(schema);
    }

    internal static void WriteTransportOp(
        ref MsgPackWriter w,
        long sessionId,
        TapDirection direction,
        long opId,
        long ticks,
        NntlTransportOpFlags flags,
        ReadOnlySpan<NntlByteRange> redactions,
        ReadOnlySpan<byte> bytes)
    {
        w.WriteArrayHeader(TransportOpFields);
        w.Write(sessionId);
        w.Write((byte)direction);
        w.Write(opId);
        w.Write(ticks);
        w.Write((byte)flags);
        w.WriteArrayHeader(redactions.Length * 2);
        foreach (var r in redactions)
        {
            w.Write(r.Start);
            w.Write(r.Length);
        }

        w.WriteBinary(bytes);
    }

    internal static void WriteGap(ref MsgPackWriter w, in NntlGap g)
    {
        w.WriteArrayHeader(GapFields);
        w.Write(g.SessionId);
        w.Write((byte)g.Direction);
        w.Write(g.FromTicks);
        w.Write(g.ToTicks);
        w.Write(g.DroppedOps);
        w.Write(g.DroppedBytes);
    }

    internal static void WriteSnapshot(ref MsgPackWriter w, long sessionId, long ticks, ReadOnlySpan<NntlSnapshotSection> sections)
    {
        w.WriteArrayHeader(SnapshotFields);
        w.Write(sessionId);
        w.Write(ticks);
        w.Write(NntlSnapshot.CurrentVersion);
        w.WriteArrayHeader(sections.Length);
        foreach (var s in sections)
        {
            w.WriteArrayHeader(2);
            w.Write((byte)s.Kind);
            w.WriteBinary(s.Data.Span);
        }
    }

    internal static void WriteSessionClose(ref MsgPackWriter w, in NntlSessionClose c)
    {
        w.WriteArrayHeader(SessionCloseFields);
        w.Write(c.SessionId);
        w.Write(c.Ticks);
        w.Write((byte)c.Kind);
        w.Write((byte)c.DisconnectReason);
    }

    internal static void WriteIndex(ref MsgPackWriter w, NntlIndex index)
    {
        var groups = index.Groups;
        w.WriteArrayHeader(IndexFields);
        w.Write(IndexVersion);
        w.WriteArrayHeader(groups.Count);
        foreach (var g in groups)
        {
            w.WriteArrayHeader(3);
            w.Write((byte)g.Kind);
            w.Write(g.SessionId);
            var entries = g.Entries;
            w.WriteBinaryHeader(checked(entries.Length * NntlFormat.IndexEntrySize));
            foreach (var e in entries)
            {
                var span = w.GetSpan(NntlFormat.IndexEntrySize);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span, e.Offset);
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), e.Ticks);
                w.Advance(NntlFormat.IndexEntrySize);
            }
        }
    }

    // ---------------------------------------------------------------- Decode

    /// <summary>Decodes a header block body.</summary>
    /// <exception cref="NntlFormatException">The body is not a valid header.</exception>
    public static NntlFileHeader DecodeHeader(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, HeaderFields, NntlBlockKind.Header);
            if (r.ReadString() != NntlFormat.Magic)
                throw new NntlFormatException("The header block does not start with the NNTL magic.");
            var header = new NntlFileHeader(
                r.ReadInt32(),
                r.ReadInt64(),
                r.ReadInt64(),
                r.ReadInt64(),
                r.ReadString() ?? "",
                r.ReadString() ?? "",
                r.ReadString());
            SkipRest(ref r, count, HeaderFields);
            if (header.StopwatchFrequency <= 0)
                throw new NntlFormatException("The header block has an invalid Stopwatch frequency.");
            return header;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.Header, e);
        }
    }

    /// <summary>Decodes a session-open block body. <see cref="NntlSessionOpen.SchemaHash"/> slices <paramref name="body"/>.</summary>
    /// <exception cref="NntlFormatException">The body is malformed.</exception>
    public static NntlSessionOpen DecodeSessionOpen(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, SessionOpenFields, NntlBlockKind.SessionOpen);
            var result = new NntlSessionOpen(
                r.ReadInt64(),
                ReadRole(ref r),
                r.ReadInt64(),
                r.ReadString() ?? "",
                r.ReadString(),
                r.ReadString(),
                r.ReadString(),
                ReadBinary(ref r, body));
            SkipRest(ref r, count, SessionOpenFields);
            return result;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.SessionOpen, e);
        }
    }

    /// <summary>Decodes a schema block body. Both fields slice <paramref name="body"/>.</summary>
    /// <exception cref="NntlFormatException">The body is malformed.</exception>
    public static NntlSchema DecodeSchema(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, SchemaFields, NntlBlockKind.Schema);
            var result = new NntlSchema(ReadBinary(ref r, body), ReadBinary(ref r, body));
            SkipRest(ref r, count, SchemaFields);
            return result;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.Schema, e);
        }
    }

    /// <summary>Decodes a transport op block body. <see cref="NntlTransportOp.Bytes"/> slices <paramref name="body"/>;
    /// redaction ranges are allocated only when present.</summary>
    /// <exception cref="NntlFormatException">The body is malformed.</exception>
    public static NntlTransportOp DecodeTransportOp(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, TransportOpFields, NntlBlockKind.TransportOp);
            var sessionId = r.ReadInt64();
            var direction = ReadDirection(ref r);
            var opId = r.ReadInt64();
            var ticks = r.ReadInt64();
            var flags = (NntlTransportOpFlags)r.ReadByte();
            var rangeValues = r.ReadArrayHeader();
            if ((rangeValues & 1) != 0)
                throw new NntlFormatException("A transport op block has an odd number of redaction values.");
            var ranges = rangeValues == 0 ? [] : new NntlByteRange[rangeValues / 2];
            for (var i = 0; i < ranges.Length; i++)
                ranges[i] = new NntlByteRange(r.ReadInt32(), r.ReadInt32());
            var bytes = ReadBinary(ref r, body);
            SkipRest(ref r, count, TransportOpFields);
            foreach (var range in ranges)
            {
                if (range.Start < 0 || range.Length < 0 || range.Start > bytes.Length - range.Length)
                    throw new NntlFormatException("A transport op block has a redaction range outside its bytes.");
            }

            return new NntlTransportOp(sessionId, direction, opId, ticks, flags, ranges, bytes);
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.TransportOp, e);
        }
    }

    /// <summary>Decodes a gap block body.</summary>
    /// <exception cref="NntlFormatException">The body is malformed.</exception>
    public static NntlGap DecodeGap(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, GapFields, NntlBlockKind.Gap);
            var result = new NntlGap(r.ReadInt64(), ReadDirection(ref r), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64());
            SkipRest(ref r, count, GapFields);
            return result;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.Gap, e);
        }
    }

    /// <summary>Decodes a snapshot block body. Section data slices <paramref name="body"/>; the section array is allocated.</summary>
    /// <remarks>Sections of kinds this build does not know are kept; consumers skip them.</remarks>
    /// <exception cref="NntlFormatException">The body is malformed.</exception>
    public static NntlSnapshot DecodeSnapshot(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, SnapshotFields, NntlBlockKind.Snapshot);
            var sessionId = r.ReadInt64();
            var ticks = r.ReadInt64();
            var version = r.ReadInt32();
            var sectionCount = r.ReadArrayHeader();
            var sections = sectionCount == 0 ? [] : new NntlSnapshotSection[sectionCount];
            for (var i = 0; i < sections.Length; i++)
            {
                var fields = r.ReadArrayHeader();
                if (fields < 2)
                    throw new NntlFormatException("A snapshot section has fewer than two fields.");
                sections[i] = new NntlSnapshotSection((NntlSnapshotSectionKind)r.ReadByte(), ReadBinary(ref r, body));
                SkipRest(ref r, fields, 2);
            }

            SkipRest(ref r, count, SnapshotFields);
            return new NntlSnapshot(sessionId, ticks, version, sections);
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.Snapshot, e);
        }
    }

    /// <summary>Decodes a session-close block body.</summary>
    /// <exception cref="NntlFormatException">The body is malformed.</exception>
    public static NntlSessionClose DecodeSessionClose(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, SessionCloseFields, NntlBlockKind.SessionClose);
            var result = new NntlSessionClose(r.ReadInt64(), r.ReadInt64(), (NntlCloseKind)r.ReadByte(), (DisconnectReason)r.ReadByte());
            SkipRest(ref r, count, SessionCloseFields);
            return result;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.SessionClose, e);
        }
    }

    internal static NntlIndex DecodeIndex(ReadOnlyMemory<byte> body)
    {
        try
        {
            var r = new MsgPackReader(body);
            var count = BeginArray(ref r, IndexFields, NntlBlockKind.Index);
            var version = r.ReadInt32();
            if (version != IndexVersion)
                throw new NntlFormatException($"Unsupported index version {version}.");
            var groups = r.ReadArrayHeader();
            var index = new NntlIndex();
            for (var i = 0; i < groups; i++)
            {
                var fields = r.ReadArrayHeader();
                if (fields < 3)
                    throw new NntlFormatException("An index group has fewer than three fields.");
                var kind = (NntlBlockKind)r.ReadByte();
                var sessionId = r.ReadInt64();
                var entries = ReadBinary(ref r, body).Span;
                if (entries.Length % NntlFormat.IndexEntrySize != 0)
                    throw new NntlFormatException("An index group has a partial entry.");
                for (var e = 0; e < entries.Length; e += NntlFormat.IndexEntrySize)
                {
                    index.Add(
                        kind,
                        sessionId,
                        System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(entries.Slice(e)),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(entries.Slice(e + 8)));
                }

                SkipRest(ref r, fields, 3);
            }

            SkipRest(ref r, count, IndexFields);
            return index;
        }
        catch (NexusSerializationException e)
        {
            throw Malformed(NntlBlockKind.Index, e);
        }
    }

    /// <summary>Reads the fields the index keys a block by: its session (0 when the kind has none) and its primary
    /// timestamp. Unknown kinds (from a later minor revision) are indexed under session 0 at tick 0.</summary>
    internal static void GetIndexKey(NntlBlockKind kind, ReadOnlyMemory<byte> body, out long sessionId, out long ticks)
    {
        switch (kind)
        {
            case NntlBlockKind.Header:
                sessionId = 0;
                ticks = DecodeHeader(body).StopwatchTicksAtAnchor;
                return;
            case NntlBlockKind.SessionOpen:
                var open = DecodeSessionOpen(body);
                (sessionId, ticks) = (open.SessionId, open.Ticks);
                return;
            case NntlBlockKind.Schema:
                DecodeSchema(body);
                (sessionId, ticks) = (0, 0);
                return;
            case NntlBlockKind.TransportOp:
                var op = DecodeTransportOp(body);
                (sessionId, ticks) = (op.SessionId, op.Ticks);
                return;
            case NntlBlockKind.Gap:
                var gap = DecodeGap(body);
                (sessionId, ticks) = (gap.SessionId, gap.FromTicks);
                return;
            case NntlBlockKind.Snapshot:
                var snapshot = DecodeSnapshot(body);
                (sessionId, ticks) = (snapshot.SessionId, snapshot.Ticks);
                return;
            case NntlBlockKind.SessionClose:
                var close = DecodeSessionClose(body);
                (sessionId, ticks) = (close.SessionId, close.Ticks);
                return;
            default:
                (sessionId, ticks) = (0, 0);
                return;
        }
    }

    private static int BeginArray(ref MsgPackReader r, int minimum, NntlBlockKind kind)
    {
        var count = r.ReadArrayHeader();
        if (count < minimum)
            throw new NntlFormatException($"A {kind} block body has {count} fields; at least {minimum} are required.");
        return count;
    }

    private static void SkipRest(ref MsgPackReader r, int count, int read)
    {
        for (var i = read; i < count; i++)
            r.Skip();
    }

    private static ReadOnlyMemory<byte> ReadBinary(ref MsgPackReader r, ReadOnlyMemory<byte> body)
    {
        var length = r.ReadBinaryHeader();
        if (length <= 0)
            return default;

        var start = (int)r.Consumed;
        r.ReadRawBytes((long)length);
        return body.Slice(start, length);
    }

    private static TapDirection ReadDirection(ref MsgPackReader r)
    {
        var value = r.ReadByte();
        if (value > (byte)TapDirection.Outbound)
            throw new NntlFormatException($"Unknown direction {value}.");
        return (TapDirection)value;
    }

    private static NntlSessionRole ReadRole(ref MsgPackReader r)
    {
        var value = r.ReadByte();
        if (value > (byte)NntlSessionRole.Client)
            throw new NntlFormatException($"Unknown session role {value}.");
        return (NntlSessionRole)value;
    }

    private static NntlFormatException Malformed(NntlBlockKind kind, Exception inner) =>
        new($"A {kind} block body is malformed.", inner);
}
