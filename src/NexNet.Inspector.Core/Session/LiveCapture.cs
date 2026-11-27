using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Session;

/// <summary>What one block did to a <see cref="LiveCapture"/>.</summary>
public enum LiveCaptureEvent : byte
{
    /// <summary>Nothing the viewer shows (a block of another session, an unknown kind).</summary>
    None = 0,

    /// <summary>The stream's header or a schema arrived.</summary>
    Prepared = 1,

    /// <summary>The attached session's open block arrived: <see cref="LiveCapture.Session"/> exists from now on.</summary>
    SessionOpened = 2,

    /// <summary>The session gained data.</summary>
    Changed = 3,

    /// <summary>The session's close block arrived.</summary>
    SessionClosed = 4,

    /// <summary>The index footer arrived: the endpoint ended the stream cleanly.</summary>
    StreamEnded = 5,
}

/// <summary>
/// Turns a live attach stream (<c>.nntl</c> blocks in order, from an inspector endpoint) into a
/// <see cref="CaptureSession"/>: header and schema blocks prepare it, the session-open block creates it, and later
/// blocks feed it under its gate. Call <see cref="Accept"/> from the one thread that reads the stream.
/// </summary>
/// <remarks>
/// An attach streams one session; blocks of any other session are ignored. Op bytes are copied into the session's
/// <see cref="LiveOpStore"/>s, because the stream reader reuses its buffer for the next block.
/// </remarks>
public sealed class LiveCapture
{
    private readonly Dictionary<ulong, NexusSchema?> _schemas = [];
    private readonly Dictionary<ulong, NntlSchema> _schemaBlocks = [];
    private readonly NexusSchema? _workspace;
    private readonly SchemaChoice _choice;
    private readonly long _maxRetainedBytes;

    // The capture-order key of each block given to the session (see CaptureSession.AddOp): ops keep it, so a save
    // interleaves both directions, gaps and the snapshot as they arrived.
    private long _sequence;

    /// <summary>Creates a capture waiting for its stream's first block.</summary>
    /// <param name="workspace">The workspace schema, if the user set one.</param>
    /// <param name="choice">Which schema decodes.</param>
    /// <param name="maxRetainedBytes">Bound on op bytes kept per direction.</param>
    public LiveCapture(NexusSchema? workspace = null, SchemaChoice choice = SchemaChoice.Embedded,
        long maxRetainedBytes = LiveOpStore.DefaultMaxRetainedBytes)
    {
        _workspace = workspace;
        _choice = choice;
        _maxRetainedBytes = maxRetainedBytes;
    }

    /// <summary>The stream's header, once read.</summary>
    public NntlFileHeader? Header { get; private set; }

    /// <summary>The attached session, once its open block arrived.</summary>
    public CaptureSession? Session { get; private set; }

    /// <summary>True once the footer arrived.</summary>
    public bool IsEnded { get; private set; }

    /// <summary>Blocks whose body did not decode, skipped.</summary>
    public int MalformedBlocks { get; private set; }

    /// <summary>Feeds one block, in stream order.</summary>
    /// <param name="block">The block; its body need only be valid during the call.</param>
    /// <returns>What it changed.</returns>
    public LiveCaptureEvent Accept(in NntlBlock block)
    {
        try
        {
            return AcceptCore(block);
        }
        catch (NntlFormatException)
        {
            MalformedBlocks++;
            return LiveCaptureEvent.None;
        }
    }

    /// <summary>
    /// Ends the capture when the stream stops without its footer (the endpoint went away): a session still open ends
    /// <see cref="CaptureState.Truncated"/>.
    /// </summary>
    public void Abandon()
    {
        if (Session is not { } session)
            return;
        lock (session.Gate)
            session.Finish(CaptureState.Truncated);
    }

    private LiveCaptureEvent AcceptCore(in NntlBlock block)
    {
        switch (block.Kind)
        {
            case NntlBlockKind.Header:
                Header = NntlCodec.DecodeHeader(block.Body);
                return LiveCaptureEvent.Prepared;

            case NntlBlockKind.Schema:
            {
                var schema = NntlCodec.DecodeSchema(block.Body);
                if (SchemaHash.TryRead(schema.Hash.Span, out var hash))
                {
                    var bytes = schema.Schema.ToArray();
                    _schemas[hash] = NexusSchema.TryRead(bytes, out var parsed, out _) ? parsed : null;
                    _schemaBlocks[hash] = new NntlSchema(schema.Hash.ToArray(), bytes);
                }
                return LiveCaptureEvent.Prepared;
            }

            case NntlBlockKind.SessionOpen:
            {
                if (Session is not null || Header is not { } header)
                    return LiveCaptureEvent.None;
                var open = NntlCodec.DecodeSessionOpen(block.Body);
                open = open with { SchemaHash = open.SchemaHash.ToArray() };
                NexusSchema? embedded = null;
                NntlSchema? schemaBlock = null;
                if (SchemaHash.TryRead(open.SchemaHash.Span, out var hash))
                {
                    _schemas.TryGetValue(hash, out embedded);
                    if (_schemaBlocks.TryGetValue(hash, out var named))
                        schemaBlock = named;
                }

                var resolution = SchemaResolution.Resolve(!open.SchemaHash.IsEmpty, embedded, _workspace, _choice);
                Session = new CaptureSession(CaptureSource.Live, header, open, new LiveOpStore(_maxRetainedBytes),
                    new LiveOpStore(_maxRetainedBytes), resolution) { SchemaBlock = schemaBlock };
                return LiveCaptureEvent.SessionOpened;
            }

            case NntlBlockKind.TransportOp:
                return Feed(NntlCodec.DecodeTransportOp(block.Body));

            case NntlBlockKind.Gap:
            {
                var gap = NntlCodec.DecodeGap(block.Body);
                if (Session is not { } session || gap.SessionId != session.SessionId)
                    return LiveCaptureEvent.None;
                lock (session.Gate)
                    session.AddGap(gap, ++_sequence);
                return LiveCaptureEvent.Changed;
            }

            case NntlBlockKind.Snapshot:
            {
                var snapshot = NntlCodec.DecodeSnapshot(block.Body);
                if (Session is not { } session || snapshot.SessionId != session.SessionId)
                    return LiveCaptureEvent.None;
                lock (session.Gate)
                    session.AddSnapshot(snapshot, ++_sequence);
                return LiveCaptureEvent.Changed;
            }

            case NntlBlockKind.SessionClose:
            {
                var close = NntlCodec.DecodeSessionClose(block.Body);
                if (Session is not { } session || close.SessionId != session.SessionId)
                    return LiveCaptureEvent.None;
                lock (session.Gate)
                    session.AddClose(close);
                return LiveCaptureEvent.SessionClosed;
            }

            case NntlBlockKind.Index:
                IsEnded = true;
                return LiveCaptureEvent.StreamEnded;

            default:
                return LiveCaptureEvent.None;
        }
    }

    private LiveCaptureEvent Feed(in NntlTransportOp op)
    {
        if (Session is not { } session || op.SessionId != session.SessionId)
            return LiveCaptureEvent.None;
        lock (session.Gate)
            session.AddOp(op, ++_sequence);
        return LiveCaptureEvent.Changed;
    }
}
