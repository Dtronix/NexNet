using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Session;

/// <summary>A frame named by direction and sequence number: the key every view selects and links by.</summary>
/// <param name="Direction">The frame's direction.</param>
/// <param name="Seq">The frame's sequence number in its direction.</param>
public readonly record struct FrameKey(TapDirection Direction, long Seq);

/// <summary>Where a gap block sits: its capture-order key and the stream offset of the first byte it lost.</summary>
/// <param name="Source">The gap's capture-order key (see <see cref="CaptureSession.AddOp"/>).</param>
/// <param name="Offset">The direction's stream offset when the gap arrived.</param>
public readonly record struct GapMark(long Source, long Offset);

/// <summary>
/// One captured session as the viewer holds it: the op stores of both directions, a frame parser per direction, the
/// message assembler and the schema decoder over them, plus the session's time range, gaps, snapshot and close. A live
/// attach and a recording feed it the same way, block by block in capture order.
/// </summary>
/// <remarks>
/// <para>
/// Threading: the feeder (a live stream's reader, a recording's indexer) runs on a background thread and calls the
/// <c>Add</c> methods while holding <see cref="Gate"/>; readers on the UI thread take <see cref="Gate"/> too while they
/// read rows (the assembler's spans are valid only until the next frame is added). Hold it briefly: the feeder waits.
/// <see cref="Version"/> and <see cref="State"/> can be read without the lock to decide whether to look.
/// </para>
/// <para>
/// Gaps: a gap block advances the direction's stream offsets by the bytes it lost and tells the parser
/// (<see cref="FrameParser.Gap{TSink}"/>) and the assembler (<see cref="MessageAssembler.Gap"/>). A direction whose
/// first op does not start with the preamble was captured mid-stream (an attach) and starts resynchronising.
/// </para>
/// </remarks>
public sealed class CaptureSession
{
    private readonly List<FrameKey> _order = [];
    private readonly List<NntlGap> _gaps = [];
    private readonly List<GapMark> _gapMarks = [];
    private readonly bool[] _fromInception = new bool[2];

    // Per direction, the first redaction that can still overlap a later frame: frames complete in stream order, so the
    // ranges before it are behind every frame to come.
    private readonly int[] _redactionCursor = new int[2];
    private FrameParser? _inboundParser;
    private FrameParser? _outboundParser;
    private long _version;
    private int _state;
    private bool _finished;

    /// <summary>Creates an empty session.</summary>
    /// <param name="source">Live or file.</param>
    /// <param name="header">The stream's or file's header (its clock anchor).</param>
    /// <param name="open">The session-open block.</param>
    /// <param name="inbound">Store for the bytes the tapped endpoint read.</param>
    /// <param name="outbound">Store for the bytes it wrote.</param>
    /// <param name="schema">The schema decision; its schema, if any, decodes bodies.</param>
    public CaptureSession(CaptureSource source, NntlFileHeader header, NntlSessionOpen open, TransportOpStore inbound,
        TransportOpStore outbound, SchemaResolution schema)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(outbound);
        Source = source;
        Header = header;
        Open = open;
        Inbound = inbound;
        Outbound = outbound;
        Schema = schema;
        Assembler = new MessageAssembler(inbound, outbound, open.Role);
        Decoder = new SessionDecoder(Assembler, schema.Schema);
        StartTicks = open.Ticks;
        EndTicks = open.Ticks;
    }

    /// <summary>Guards everything but <see cref="Version"/> and <see cref="State"/>; see the remarks.</summary>
    public Lock Gate { get; } = new();

    /// <summary>Live or file.</summary>
    public CaptureSource Source { get; }

    /// <summary>The header: Stopwatch frequency and the wall-clock anchor of every tick value here.</summary>
    public NntlFileHeader Header { get; }

    /// <summary>The session-open block.</summary>
    public NntlSessionOpen Open { get; }

    /// <summary>The session's id in its stream or file.</summary>
    public long SessionId => Open.SessionId;

    /// <summary>Server or client side.</summary>
    public NntlSessionRole Role => Open.Role;

    /// <summary>Which schema decodes the session, and how the embedded one compares with the workspace's.</summary>
    public SchemaResolution Schema { get; }

    /// <summary>The assembled session.</summary>
    public MessageAssembler Assembler { get; }

    /// <summary>Decodes the assembler's rows with <see cref="Schema"/> (raw without one).</summary>
    public SessionDecoder Decoder { get; }

    /// <summary>Bytes the tapped endpoint read.</summary>
    public TransportOpStore Inbound { get; }

    /// <summary>Bytes the tapped endpoint wrote.</summary>
    public TransportOpStore Outbound { get; }

    /// <summary>Increments on every change; read without the lock to see whether anything changed.</summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>Where the session stands; read without the lock.</summary>
    public CaptureState State => (CaptureState)Volatile.Read(ref _state);

    /// <summary>Stopwatch ticks when the capture of the session began (its open block).</summary>
    public long StartTicks { get; private set; }

    /// <summary>Stopwatch ticks of the latest op, gap or close.</summary>
    public long EndTicks { get; private set; }

    /// <summary>Every frame of both directions, in the order the parsers completed them.</summary>
    public ReadOnlySpan<FrameKey> FrameOrder => CollectionsMarshal.AsSpan(_order);

    /// <summary>Frames of both directions.</summary>
    public int FrameCount => _order.Count;

    /// <summary>Gap blocks: ops the capture dropped, per direction, in capture order.</summary>
    public ReadOnlySpan<NntlGap> Gaps => CollectionsMarshal.AsSpan(_gaps);

    /// <summary>The attach snapshot, decoded; null when the session was captured from its start or without one.</summary>
    public SnapshotSeed? Snapshot { get; private set; }

    /// <summary>Stopwatch ticks of the snapshot.</summary>
    public long SnapshotTicks { get; private set; }

    /// <summary>Snapshot sections that did not decode (left out of <see cref="Snapshot"/>).</summary>
    public int MalformedSnapshotSections { get; private set; }

    /// <summary>
    /// The attach snapshot block as it arrived (its sections copied), so a save writes it back; null without one.
    /// </summary>
    public NntlSnapshot? SnapshotBlock { get; private set; }

    /// <summary>Capture-order key of the snapshot block (see <see cref="AddOp"/>'s source).</summary>
    public long SnapshotSource { get; private set; }

    /// <summary>
    /// The schema block <see cref="NntlSessionOpen.SchemaHash"/> names, with its bytes, so a save embeds the same schema;
    /// null when the session names none or it was not found.
    /// </summary>
    public NntlSchema? SchemaBlock { get; init; }

    /// <summary>The session-close block, once seen.</summary>
    public NntlSessionClose? Close { get; private set; }

    /// <summary>Transport ops of both directions.</summary>
    public long OpCount { get; private set; }

    /// <summary>Bytes of both directions, gaps excluded.</summary>
    public long ByteCount => Inbound.ByteCount + Outbound.ByteCount;

    /// <summary>True once a direction's first op started with the protocol preamble: it was captured from inception.</summary>
    /// <param name="direction">The direction.</param>
    public bool IsFromInception(TapDirection direction) => _fromInception[(int)direction];

    /// <summary>The parser of a direction; null before its first op or gap.</summary>
    /// <param name="direction">The direction.</param>
    public FrameParser? GetParser(TapDirection direction)
        => direction == TapDirection.Inbound ? _inboundParser : _outboundParser;

    /// <summary>The store of a direction.</summary>
    /// <param name="direction">The direction.</param>
    public TransportOpStore GetStore(TapDirection direction)
        => direction == TapDirection.Inbound ? Inbound : Outbound;

    /// <summary>Gets a frame, with the assembler's flags.</summary>
    /// <param name="key">The frame.</param>
    /// <param name="frame">The frame.</param>
    /// <returns>False for an unknown frame.</returns>
    public bool TryGetFrame(FrameKey key, out FrameRecord frame) => Assembler.TryGetFrame(key.Direction, key.Seq, out frame);

    /// <summary>For each of <see cref="Gaps"/>, its capture-order key and the stream offset where its lost bytes begin.</summary>
    public ReadOnlySpan<GapMark> GapMarks => CollectionsMarshal.AsSpan(_gapMarks);

    /// <summary>Adds a transport op. Call under <see cref="Gate"/>.</summary>
    /// <param name="op">The op. Its bytes need only be valid during the call (the store copies what it keeps).</param>
    /// <param name="source">
    /// The op's capture-order key, kept as <see cref="StreamOp.Source"/>: its block offset in a recording (which is where a
    /// file store reads its bytes back), the block's sequence number in a live stream. Both rise in capture order, so a
    /// save can interleave the two directions, gaps and the snapshot exactly as they arrived.
    /// </param>
    public void AddOp(in NntlTransportOp op, long source)
    {
        if (_finished || op.SessionId != SessionId)
            return;

        var parser = ParserFor(op.Direction, op.Bytes.Span);
        GetStore(op.Direction).Append(op, source);
        parser.Push(op.OpId, op.Ticks, op.Bytes.Span, new Sink(this));
        OpCount++;
        Touch(op.Ticks);
    }

    /// <summary>Adds a gap block. Call under <see cref="Gate"/>.</summary>
    /// <param name="gap">The gap.</param>
    /// <param name="source">Its capture-order key; see <see cref="AddOp"/>.</param>
    public void AddGap(in NntlGap gap, long source = 0)
    {
        if (_finished || gap.SessionId != SessionId)
            return;

        _gaps.Add(gap);
        var store = GetStore(gap.Direction);
        _gapMarks.Add(new GapMark(source, store.EndOffset));
        var parser = GetParser(gap.Direction);
        if (parser is null)
        {
            // Nothing of this direction arrived yet: the parser starts after the gap, resynchronising.
            store.SkipGap(gap.DroppedBytes);
            SetParser(gap.Direction, new FrameParser(gap.Direction, FrameParserStart.Resync, store.EndOffset));
        }
        else
        {
            store.SkipGap(gap.DroppedBytes);
            parser.Gap(gap.DroppedBytes, new Sink(this));
        }

        Assembler.Gap(gap.Direction);
        Touch(gap.ToTicks);
    }

    /// <summary>
    /// Adds the attach snapshot. Call under <see cref="Gate"/>. Before any frame it seeds the assembler
    /// (<see cref="MessageAssembler.Seed"/>); later it is only kept for display.
    /// </summary>
    /// <param name="snapshot">The snapshot block; its sections need only be valid during the call.</param>
    /// <param name="source">Its capture-order key; see <see cref="AddOp"/>.</param>
    public void AddSnapshot(in NntlSnapshot snapshot, long source = 0)
    {
        if (_finished || snapshot.SessionId != SessionId)
            return;

        // The reader's buffer is reused for the next block; a snapshot is one small block per attach.
        var sections = snapshot.Sections.ToArray();
        for (var i = 0; i < sections.Length; i++)
            sections[i] = sections[i] with { Data = sections[i].Data.ToArray() };
        SnapshotBlock = snapshot with { Sections = sections };
        SnapshotSource = source;

        Snapshot = SnapshotSeed.FromSnapshot(snapshot, out var malformed);
        MalformedSnapshotSections = malformed;
        SnapshotTicks = snapshot.Ticks;
        if (_order.Count == 0)
            Assembler.Seed(Snapshot);
        Touch(snapshot.Ticks);
    }

    /// <summary>Adds the session-close block and finishes the session. Call under <see cref="Gate"/>.</summary>
    /// <param name="close">The close block.</param>
    public void AddClose(in NntlSessionClose close)
    {
        if (_finished || close.SessionId != SessionId)
            return;

        Close = close;
        Touch(close.Ticks);
        Finish(close.Kind == NntlCloseKind.Disconnected ? CaptureState.Ended : CaptureState.Stopped);
    }

    /// <summary>
    /// Ends the session without (more) data: the parsers emit their frames in progress as incomplete and the
    /// assembler truncates what was still assembling. Call under <see cref="Gate"/>. Later blocks are ignored.
    /// </summary>
    /// <param name="state">How it ended; <see cref="CaptureState.Truncated"/> when the stream simply stopped.</param>
    public void Finish(CaptureState state)
    {
        if (_finished)
            return;

        _finished = true;
        var sink = new Sink(this);
        _outboundParser?.Complete(sink);
        _inboundParser?.Complete(sink);
        Assembler.Complete();
        Volatile.Write(ref _state, (int)state);
        Interlocked.Increment(ref _version);
    }

    private FrameParser ParserFor(TapDirection direction, ReadOnlySpan<byte> firstOp)
    {
        var parser = GetParser(direction);
        if (parser is not null)
            return parser;

        // A direction captured from the connection's first byte starts with the preamble; anything else joined
        // mid-stream and cannot trust its first byte to start a frame.
        var fromInception = firstOp.StartsWith(FrameLayout.PreambleMagic);
        _fromInception[(int)direction] = fromInception;
        parser = new FrameParser(direction, fromInception ? FrameParserStart.Preamble : FrameParserStart.Resync,
            GetStore(direction).EndOffset);
        SetParser(direction, parser);
        return parser;
    }

    private void SetParser(TapDirection direction, FrameParser parser)
    {
        if (direction == TapDirection.Inbound)
            _inboundParser = parser;
        else
            _outboundParser = parser;
    }

    private void Touch(long ticks)
    {
        if (ticks > EndTicks)
            EndTicks = ticks;
        if (ticks < StartTicks)
            StartTicks = ticks;
        Interlocked.Increment(ref _version);
    }

    // True when a redaction range of the frame's direction overlaps the frame's bytes. The op stores learn the ranges
    // when the op is appended, before the parser emits the frames the op completes.
    private bool IsRedacted(in FrameRecord frame)
    {
        var ranges = GetStore(frame.Direction).Redactions;
        ref var cursor = ref _redactionCursor[(int)frame.Direction];
        while (cursor < ranges.Length && ranges[cursor].End <= frame.Offset)
            cursor++;
        return cursor < ranges.Length && ranges[cursor].Offset < frame.Offset + frame.Length;
    }

    private readonly struct Sink(CaptureSession session) : IFrameSink
    {
        public void Add(in FrameRecord frame)
        {
            session._order.Add(new FrameKey(frame.Direction, frame.Seq));
            if (frame.Length > 0 && session.IsRedacted(frame))
                session.Assembler.Add(frame with { Flags = frame.Flags | FrameFlags.Redacted });
            else
                session.Assembler.Add(frame);
        }
    }
}
