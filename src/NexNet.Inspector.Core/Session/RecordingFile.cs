using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Schema;

namespace NexNet.Inspector.Core.Session;

/// <summary>What the index says about one recorded session, without reading its ops (the open-recording session table).</summary>
/// <param name="Open">The session-open block.</param>
/// <param name="Ops">Transport op blocks.</param>
/// <param name="Gaps">Gap blocks.</param>
/// <param name="HasSnapshot">True when it starts with an attach snapshot.</param>
/// <param name="HasClose">True when its close block was recorded.</param>
/// <param name="LastTicks">Ticks of its latest indexed block.</param>
public readonly record struct RecordedSessionSummary(
    NntlSessionOpen Open,
    int Ops,
    int Gaps,
    bool HasSnapshot,
    bool HasClose,
    long LastTicks)
{
    /// <summary>Ticks from the open block to the latest block.</summary>
    public long DurationTicks => Math.Max(0, LastTicks - Open.Ticks);
}

/// <summary>Progress of a background index of a recorded session.</summary>
/// <param name="Blocks">Blocks of the session read so far.</param>
/// <param name="TotalBlocks">Blocks of the session in the index.</param>
public readonly record struct IndexingProgress(int Blocks, int TotalBlocks)
{
    /// <summary>Done fraction, 0 to 1.</summary>
    public double Fraction => TotalBlocks == 0 ? 1 : (double)Blocks / TotalBlocks;
}

/// <summary>
/// An open <c>.nntl</c> recording: its header, index, damage and sessions, and the means to open one session as a
/// <see cref="CaptureSession"/> and index it on a background thread. Readable blocks always open (OQ3): a missing
/// footer or damaged regions only raise <see cref="Warnings"/>.
/// </summary>
/// <remarks>
/// The file stays open while any session opened from it is in use: session stores read op bytes back on demand.
/// Dispose it after those sessions.
/// </remarks>
public sealed class RecordingFile : IDisposable
{
    // Blocks read per lock of the session: indexing holds the session's gate for one batch, so a viewer waits at most
    // that long.
    private const int BatchBlocks = 64;

    private readonly Dictionary<ulong, NexusSchema?> _schemas = [];
    private readonly Lock _schemaGate = new();

    private RecordingFile(SharedNntlReader reader, string? path)
    {
        Reader = reader;
        Path = path;
        Warnings = (reader.IndexSource == NntlIndexSource.Scan ? RecordingWarnings.IndexFromScan : RecordingWarnings.None)
                   | (reader.Damage.Count > 0 ? RecordingWarnings.Damaged : RecordingWarnings.None);
    }

    /// <summary>Opens a recording file for reading (shared, so a recording still being written can be viewed).</summary>
    /// <param name="path">The file.</param>
    /// <exception cref="NntlFormatException">The file does not start with a valid header block.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static RecordingFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.RandomAccess);
        try
        {
            return new RecordingFile(new SharedNntlReader(NntlReader.Open(stream)), System.IO.Path.GetFullPath(path));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens a recording held in a stream.</summary>
    /// <param name="stream">Readable, seekable; owned by the recording from now on.</param>
    /// <param name="path">A path to show for it, if any.</param>
    /// <exception cref="NntlFormatException">The stream does not start with a valid header block.</exception>
    public static RecordingFile Open(Stream stream, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new RecordingFile(new SharedNntlReader(NntlReader.Open(stream)), path);
    }

    /// <summary>The full path, when opened from a file.</summary>
    public string? Path { get; }

    /// <summary>The reader, shared by the indexer and the sessions' stores.</summary>
    public SharedNntlReader Reader { get; }

    /// <summary>The file header.</summary>
    public NntlFileHeader Header => Reader.Header;

    /// <summary>Footer, or a scan.</summary>
    public NntlIndexSource IndexSource => Reader.IndexSource;

    /// <summary>Regions that could not be read.</summary>
    public IReadOnlyList<NntlDamage> Damage => Reader.Damage;

    /// <summary>File length in bytes.</summary>
    public long Length => Reader.Length;

    /// <summary>The recorded sessions, in file order.</summary>
    public IReadOnlyList<NntlSessionOpen> Sessions => Reader.Sessions;

    /// <summary>What to warn about.</summary>
    public RecordingWarnings Warnings { get; }

    /// <summary>Summarizes a session from the index alone.</summary>
    /// <param name="open">One of <see cref="Sessions"/>.</param>
    public RecordedSessionSummary Summarize(in NntlSessionOpen open)
    {
        var index = Reader.Index;
        var id = open.SessionId;
        var ops = index.GetEntries(NntlBlockKind.TransportOp, id);
        var last = open.Ticks;
        foreach (var kind in SessionBlockKinds)
        {
            foreach (var entry in index.GetEntries(kind, id))
                last = Math.Max(last, entry.Ticks);
        }

        return new RecordedSessionSummary(open, ops.Length, index.GetEntries(NntlBlockKind.Gap, id).Length,
            index.GetEntries(NntlBlockKind.Snapshot, id).Length > 0, index.GetEntries(NntlBlockKind.SessionClose, id).Length > 0,
            last);
    }

    /// <summary>The embedded schema a session names, read once per hash.</summary>
    /// <param name="open">The session.</param>
    /// <returns>Null when the session names none, or it is missing or unreadable.</returns>
    public NexusSchema? GetEmbeddedSchema(in NntlSessionOpen open)
    {
        if (!SchemaHash.TryRead(open.SchemaHash.Span, out var hash))
            return null;

        lock (_schemaGate)
        {
            if (_schemas.TryGetValue(hash, out var cached))
                return cached;

            NexusSchema? schema = Reader.TryGetSchema(open.SchemaHash.Span, out var embedded)
                                  && NexusSchema.TryRead(embedded.Schema, out var parsed, out _)
                ? parsed
                : null;
            _schemas[hash] = schema;
            return schema;
        }
    }

    /// <summary>Resolves the schema of a session.</summary>
    /// <param name="open">The session.</param>
    /// <param name="workspace">The workspace schema, if the user set one.</param>
    /// <param name="choice">The user's choice.</param>
    public SchemaResolution ResolveSchema(in NntlSessionOpen open, NexusSchema? workspace, SchemaChoice choice)
        => SchemaResolution.Resolve(!open.SchemaHash.IsEmpty, GetEmbeddedSchema(open), workspace, choice);

    /// <summary>Creates an empty session for one recorded session; fill it with <see cref="IndexAsync"/>.</summary>
    /// <param name="sessionId">The session's id.</param>
    /// <param name="workspace">The workspace schema, if any.</param>
    /// <param name="choice">Which schema decodes.</param>
    /// <param name="cacheCapacity">Op byte cache bound per direction.</param>
    /// <exception cref="ArgumentException">No such session in the file.</exception>
    public CaptureSession CreateSession(long sessionId, NexusSchema? workspace = null, SchemaChoice choice = SchemaChoice.Embedded,
        long cacheCapacity = RecordingOpStore.DefaultCacheCapacity)
    {
        foreach (var open in Sessions)
        {
            if (open.SessionId != sessionId)
                continue;
            NntlSchema? block = null;
            if (!open.SchemaHash.IsEmpty && Reader.TryGetSchema(open.SchemaHash.Span, out var embedded))
                block = new NntlSchema(embedded.Hash.ToArray(), embedded.Schema.ToArray());
            return new CaptureSession(CaptureSource.File, Header, open, new RecordingOpStore(Reader, cacheCapacity),
                new RecordingOpStore(Reader, cacheCapacity), ResolveSchema(open, workspace, choice)) { SchemaBlock = block };
        }

        throw new ArgumentException($"The recording has no session {sessionId}.", nameof(sessionId));
    }

    /// <summary>
    /// Reads every block of a session in file order into it, on the calling thread (run it on a background one). The
    /// session's gate is held per batch of blocks, so viewers can read the frames indexed so far meanwhile. A
    /// session whose close block is missing ends <see cref="CaptureState.Truncated"/>.
    /// </summary>
    /// <param name="session">A session from <see cref="CreateSession"/>.</param>
    /// <param name="progress">Called after each batch with the progress so far, on the indexing thread.</param>
    /// <param name="cancellationToken">Stops indexing; the session keeps what was read.</param>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    public void Index(CaptureSession session, Action<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var blocks = SessionBlocks(session.SessionId);
        var batch = new NntlBlock[BatchBlocks];
        var done = 0;
        progress?.Invoke(new IndexingProgress(0, blocks.Length));
        while (done < blocks.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read outside the session's gate: disk time never holds up a viewer.
            var count = 0;
            var end = Math.Min(blocks.Length, done + BatchBlocks);
            for (var i = done; i < end; i++)
            {
                if (Reader.TryReadBlock(blocks[i].Offset, out var block))
                    batch[count++] = block;
            }

            lock (session.Gate)
            {
                for (var i = 0; i < count; i++)
                    Apply(session, batch[i]);
            }

            Array.Clear(batch, 0, count);
            done = end;
            progress?.Invoke(new IndexingProgress(done, blocks.Length));
        }

        lock (session.Gate)
            session.Finish(CaptureState.Truncated);
    }

    /// <summary>Runs <see cref="Index"/> on the thread pool.</summary>
    /// <inheritdoc cref="Index"/>
    public Task IndexAsync(CaptureSession session, Action<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Index(session, progress, cancellationToken), cancellationToken);

    /// <summary>Closes the file.</summary>
    public void Dispose() => Reader.Dispose();

    private static readonly NntlBlockKind[] SessionBlockKinds =
    [
        NntlBlockKind.TransportOp,
        NntlBlockKind.Gap,
        NntlBlockKind.Snapshot,
        NntlBlockKind.SessionClose,
    ];

    // The session's blocks in file order, which is capture order.
    private NntlBlockRef[] SessionBlocks(long sessionId)
    {
        var index = Reader.Index;
        var count = 0;
        foreach (var kind in SessionBlockKinds)
            count += index.GetEntries(kind, sessionId).Length;

        var blocks = new NntlBlockRef[count];
        var i = 0;
        foreach (var kind in SessionBlockKinds)
        {
            foreach (var entry in index.GetEntries(kind, sessionId))
                blocks[i++] = new NntlBlockRef(entry.Offset, kind, sessionId, entry.Ticks);
        }

        Array.Sort(blocks, static (a, b) => a.Offset.CompareTo(b.Offset));
        return blocks;
    }

    private static void Apply(CaptureSession session, in NntlBlock block)
    {
        try
        {
            switch (block.Kind)
            {
                case NntlBlockKind.TransportOp:
                    session.AddOp(NntlCodec.DecodeTransportOp(block.Body), block.Offset);
                    break;
                case NntlBlockKind.Gap:
                    session.AddGap(NntlCodec.DecodeGap(block.Body), block.Offset);
                    break;
                case NntlBlockKind.Snapshot:
                    session.AddSnapshot(NntlCodec.DecodeSnapshot(block.Body), block.Offset);
                    break;
                case NntlBlockKind.SessionClose:
                    session.AddClose(NntlCodec.DecodeSessionClose(block.Body));
                    break;
            }
        }
        catch (NntlFormatException)
        {
            // A block whose checksum held but whose body does not decode is skipped, like the reader's own damage.
        }
    }
}
