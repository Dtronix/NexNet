using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Sample;

/// <summary>
/// A recording read the way the inspector reads one: every block through <see cref="NntlReader"/>, each session's
/// transport ops through the frame parser (I02), the message assembler (I03) and, with the embedded schema, the
/// session decoder (I07).
/// </summary>
public sealed class RecordingAnalysis
{
    private RecordingAnalysis(NntlFileHeader header, NntlIndexSource indexSource, IReadOnlyList<NntlDamage> damage,
        long length, IReadOnlyList<SessionAnalysis> sessions, int schemaBlocks)
    {
        Header = header;
        IndexSource = indexSource;
        Damage = damage;
        Length = length;
        Sessions = sessions;
        SchemaBlocks = schemaBlocks;
    }

    /// <summary>The file header.</summary>
    public NntlFileHeader Header { get; }

    /// <summary>Footer, or a scan when the footer was missing.</summary>
    public NntlIndexSource IndexSource { get; }

    /// <summary>What the reader skipped.</summary>
    public IReadOnlyList<NntlDamage> Damage { get; }

    /// <summary>File length in bytes.</summary>
    public long Length { get; }

    /// <summary>Embedded schema blocks.</summary>
    public int SchemaBlocks { get; }

    /// <summary>Sessions in the order their open block appears.</summary>
    public IReadOnlyList<SessionAnalysis> Sessions { get; }

    /// <summary>Reads and replays a recording.</summary>
    public static RecordingAnalysis Read(byte[] file)
    {
        using var reader = NntlReader.Open(new MemoryStream(file, writable: false));
        var sessions = new Dictionary<long, SessionAnalysis>();
        var ordered = new List<SessionAnalysis>();
        var schemas = new Dictionary<ulong, NexusSchema?>();
        foreach (var open in reader.Sessions)
        {
            NexusSchema? schema = null;
            if (SchemaHash.TryRead(open.SchemaHash.Span, out var hash))
            {
                if (!schemas.TryGetValue(hash, out schema))
                {
                    schema = reader.TryGetSchema(open.SchemaHash.Span, out var embedded)
                             && NexusSchema.TryRead(embedded.Schema, out var parsed, out _)
                        ? parsed
                        : null;
                    schemas[hash] = schema;
                }
            }

            var session = new SessionAnalysis(open, schema);
            sessions[open.SessionId] = session;
            ordered.Add(session);
        }

        foreach (var entry in reader.Index.ToFileOrder())
        {
            if (!NntlIndex.HasSession(entry.Kind) || !sessions.TryGetValue(entry.SessionId, out var session))
                continue;
            if (!reader.TryReadBlock(entry.Offset, out var block))
                continue;

            switch (block.Kind)
            {
                case NntlBlockKind.TransportOp:
                    session.Add(NntlCodec.DecodeTransportOp(block.Body));
                    break;
                case NntlBlockKind.Gap:
                    session.Gaps.Add(NntlCodec.DecodeGap(block.Body));
                    break;
                case NntlBlockKind.Snapshot:
                    session.HasSnapshot = true;
                    break;
                case NntlBlockKind.SessionClose:
                    session.Close = NntlCodec.DecodeSessionClose(block.Body);
                    break;
            }
        }

        foreach (var session in ordered)
            session.Complete();

        return new RecordingAnalysis(reader.Header, reader.IndexSource, reader.Damage.ToArray(), reader.Length, ordered,
            reader.Index.GetEntries(NntlBlockKind.Schema).Length);
    }
}

/// <summary>One recorded session, replayed.</summary>
public sealed class SessionAnalysis
{
    private static ReadOnlySpan<byte> Magic => "NnP"u8;

    private readonly InMemoryStreamBytes _inbound = new();
    private readonly InMemoryStreamBytes _outbound = new();
    private FrameParser? _inboundParser;
    private FrameParser? _outboundParser;

    internal SessionAnalysis(NntlSessionOpen open, NexusSchema? schema)
    {
        Open = open;
        Assembler = new MessageAssembler(_inbound, _outbound, open.Role);
        Decoder = new SessionDecoder(Assembler, schema);
    }

    /// <summary>The session-open block.</summary>
    public NntlSessionOpen Open { get; }

    /// <summary>The embedded schema the session names, when it was found and parsed.</summary>
    public NexusSchema? Schema => Decoder.Schema;

    /// <summary>The assembled session.</summary>
    public MessageAssembler Assembler { get; }

    /// <summary>The decoder over <see cref="Assembler"/>.</summary>
    public SessionDecoder Decoder { get; }

    /// <summary>True when the session starts with a snapshot (it was already connected when recording began).</summary>
    public bool HasSnapshot { get; internal set; }

    /// <summary>The session-close block, when recorded.</summary>
    public NntlSessionClose? Close { get; internal set; }

    /// <summary>Gap blocks.</summary>
    public List<NntlGap> Gaps { get; } = [];

    /// <summary>Transport ops per direction.</summary>
    public int InboundOps { get; private set; }

    /// <inheritdoc cref="InboundOps"/>
    public int OutboundOps { get; private set; }

    /// <summary>Bytes of the first op of each direction.</summary>
    public int FirstInboundOpLength { get; private set; } = -1;

    /// <inheritdoc cref="FirstInboundOpLength"/>
    public int FirstOutboundOpLength { get; private set; } = -1;

    /// <summary>Bytes the tap flagged as redacted, per direction.</summary>
    public int InboundRedactedBytes { get; private set; }

    /// <inheritdoc cref="InboundRedactedBytes"/>
    public int OutboundRedactedBytes { get; private set; }

    /// <summary>Redacted bytes in the first op of each direction.</summary>
    public int FirstInboundOpRedactedBytes { get; private set; }

    /// <inheritdoc cref="FirstInboundOpRedactedBytes"/>
    public int FirstOutboundOpRedactedBytes { get; private set; }

    /// <summary>True when the direction's first op starts with the protocol preamble (recorded from inception).</summary>
    public bool InboundFromInception { get; private set; }

    /// <inheritdoc cref="InboundFromInception"/>
    public bool OutboundFromInception { get; private set; }

    /// <summary>True when a direction's bytes stopped framing.</summary>
    public bool InboundFaulted => _inboundParser?.IsFaulted ?? false;

    /// <inheritdoc cref="InboundFaulted"/>
    public bool OutboundFaulted => _outboundParser?.IsFaulted ?? false;

    /// <summary>The method an invocation calls, or null.</summary>
    public string? MethodName(in InvocationRow invocation) =>
        Decoder.TryGetMethod(invocation, out var method) ? method.Name : null;

    /// <summary>Invocations the client made, of one method.</summary>
    public InvocationRow[] ClientCalls(string method)
    {
        var result = new List<InvocationRow>();
        foreach (var invocation in Assembler.Invocations)
        {
            if (Decoder.IsClientCall(invocation) == true && MethodName(invocation) == method)
                result.Add(invocation);
        }

        return result.ToArray();
    }

    /// <summary>The first field named <paramref name="name"/> in a decoded value.</summary>
    public static bool TryFindField(DecodedValue value, string name, out DecodedField field)
    {
        foreach (var candidate in value.Fields)
        {
            if (candidate.Name == name)
            {
                field = candidate;
                return true;
            }
        }

        field = default;
        return false;
    }

    internal void Add(in NntlTransportOp op)
    {
        var bytes = op.Bytes.ToArray();
        var redacted = 0;
        foreach (var range in op.Redactions.Span)
            redacted += range.Length;

        if (op.Direction == TapDirection.Inbound)
        {
            if (_inboundParser == null)
            {
                InboundFromInception = bytes.AsSpan().StartsWith(Magic);
                FirstInboundOpLength = bytes.Length;
                FirstInboundOpRedactedBytes = redacted;
                _inboundParser = new FrameParser(TapDirection.Inbound, InboundFromInception);
            }

            InboundOps++;
            InboundRedactedBytes += redacted;
            _inbound.Append(bytes);
            _inboundParser.Push(op.OpId, op.Ticks, bytes, Assembler);
        }
        else
        {
            if (_outboundParser == null)
            {
                OutboundFromInception = bytes.AsSpan().StartsWith(Magic);
                FirstOutboundOpLength = bytes.Length;
                FirstOutboundOpRedactedBytes = redacted;
                _outboundParser = new FrameParser(TapDirection.Outbound, OutboundFromInception);
            }

            OutboundOps++;
            OutboundRedactedBytes += redacted;
            _outbound.Append(bytes);
            _outboundParser.Push(op.OpId, op.Ticks, bytes, Assembler);
        }
    }

    internal void Complete()
    {
        _outboundParser?.Complete(Assembler);
        _inboundParser?.Complete(Assembler);
        Assembler.Complete();
    }
}
