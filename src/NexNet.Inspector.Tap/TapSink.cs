using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;

namespace NexNet.Inspector.Tap;

/// <summary>Dropped ops of one session direction not yet reported as a gap. Guarded by the sink's lock.</summary>
internal struct GapState
{
    public long Ops;
    public long Bytes;
    public long FromTicks;
    public long ToTicks;

    public void Add(long bytes, long ticks)
    {
        if (Ops == 0)
            FromTicks = ticks;
        ToTicks = ticks;
        Ops++;
        Bytes += bytes;
    }
}

/// <summary>
/// An op a Block-mode producer is waiting to place: the bytes stay borrowed from the session until <see cref="Done"/>
/// is set, so the sender copies them in when the ring has room. Guarded by the sink's lock.
/// </summary>
internal sealed class PendingOp
{
    public readonly AsyncSignal Done = new();
    public bool Active;
    public bool Direct;
    public ReadOnlySequence<byte> Data;
    public long OpId;
    public long Ticks;
    public int RedactionStart;
    public int RedactionLength;
}

/// <summary>One session feeding one sink.</summary>
internal sealed class SinkLink(TapSink sink, TappedSession session)
{
    public readonly TapSink Sink = sink;
    public readonly TappedSession Session = session;
    public readonly GapState[] Gaps = new GapState[2];
    public readonly PendingOp[] Pending = [new(), new()];

    /// <summary>False once the session closed or the sink stopped: later ops are ignored. Guarded by the sink's lock.</summary>
    public bool Open = true;

    public long SessionId => Session.Id;
}

internal enum ControlKind : byte
{
    Open,
    Close,
}

/// <summary>A session-open or session-close event, kept in order with the session's ops through a ring marker.</summary>
internal sealed class ControlEvent(ControlKind kind, SinkLink link)
{
    public readonly ControlKind Kind = kind;
    public readonly SinkLink Link = link;
    public NntlSessionOpen Open;
    public NntlSnapshotSection[]? Snapshot;
    public long SnapshotTicks;
    public NntlSessionClose Close;
    public GapState InboundGap;
    public GapState OutboundGap;
}

/// <summary>A session written at the start of a sink's stream: open block and optional snapshot.</summary>
internal readonly record struct InitialSession(NntlSessionOpen Open, NntlSnapshotSection[]? Snapshot, long SnapshotTicks);

/// <summary>
/// One viewer or recorder: a bounded byte ring that the tapped sessions copy their ops into, drained by a background
/// sender that writes them as <c>.nntl</c> blocks to the output.
/// </summary>
/// <remarks>
/// <para>
/// The ring is allocated once. A record is a 48-byte header plus the op's bytes, contiguous (a record that would cross
/// the end leaves a wrap marker and starts at 0), so the sender hands ring bytes to the writer without copying.
/// Producers (each session's read loop and write path) reserve and copy under the sink's lock; the sender reads the
/// committed region outside it, since producers only ever write into free space.
/// </para>
/// <para>
/// Overflow: in Drop mode an op that does not fit is counted into the session direction's gap, which is written as
/// a gap record ahead of that direction's next op that fits (or when the sender frees space). In Block mode the
/// producer gets a pending task and the sender copies the op in once there is room; an op larger than the whole
/// ring is written straight from the borrowed bytes once the ring is empty. Session open and close events travel
/// as markers in the ring so they stay in order with the ops.
/// </para>
/// </remarks>
internal sealed class TapSink
{
    private const int RecordHeaderSize = 48;
    private static readonly TimeSpan FinalFlushTimeout = TimeSpan.FromSeconds(5);

    private enum RecordKind : byte
    {
        Op = 1,
        Gap = 2,
        Control = 3,
        Wrap = 4,
    }

    private readonly NexusInspector _inspector;
    private readonly SinkOutput _output;
    private readonly bool _single;
    private readonly bool _block;
    private readonly byte[] _ring;
    private readonly Lock _gate = new();
    private readonly AsyncSignal _wake = new();
    private readonly List<SinkLink> _links = [];
    private readonly Queue<ControlEvent> _controls = new();
    private readonly List<PendingOp> _released = [];
    private readonly NntlByteRange[] _range = new NntlByteRange[1];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private long _head;
    private long _tail;
    private int _deferredMarkers;
    private int _blocked;
    private bool _senderWaiting;
    private bool _stopping;
    private NntlWriter? _writer;
    private byte[] _scratch = [];

    /// <param name="inspector">Owner, told when the sink ends.</param>
    /// <param name="output">Destination.</param>
    /// <param name="single">True for an attach to one session: the stream ends when that session closes.</param>
    /// <param name="overflow">Drop or Block.</param>
    /// <param name="capacity">Ring size in bytes.</param>
    public TapSink(NexusInspector inspector, SinkOutput output, bool single, InspectorOverflow overflow, int capacity)
    {
        _inspector = inspector;
        _output = output;
        _single = single;
        _block = overflow == InspectorOverflow.Block;
        _ring = new byte[capacity & ~7];
    }

    /// <summary>Completes once the stream has ended and the output is complete.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Error that ended the stream early, if any.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Creates a link for <paramref name="session"/>; with <paramref name="open"/>, queues its open block in order with its ops.</summary>
    public SinkLink CreateLink(TappedSession session, in NntlSessionOpen? open)
    {
        var link = new SinkLink(this, session);
        var wake = false;
        lock (_gate)
        {
            _links.Add(link);
            if (open is { } o)
            {
                EnqueueControlLocked(new ControlEvent(ControlKind.Open, link) { Open = o });
                wake = TakeWakeLocked();
            }
        }

        if (wake)
            _wake.Set();
        return link;
    }

    /// <summary>Forgets a link that never received an op (its session closed before it was added).</summary>
    public void DropLink(SinkLink link)
    {
        lock (_gate)
        {
            link.Open = false;
            _links.Remove(link);
        }
    }

    /// <summary>Starts the sender.</summary>
    public void Start(InitialSession[] initial) => _ = Task.Run(() => RunAsync(initial));

    /// <summary>Copies one op into the ring, or counts it as dropped, or (Block) returns a task that holds the producer until it is placed.</summary>
    public ValueTask EnqueueOp(SinkLink link, TapDirection direction, long opId, long ticks, in ReadOnlySequence<byte> data,
        int redactionStart, int redactionLength)
    {
        var length = data.Length;
        var size = RecordSize(length);
        var wait = ValueTask.CompletedTask;
        bool wake;
        lock (_gate)
        {
            if (_stopping || !link.Open)
                return ValueTask.CompletedTask;

            ref var gap = ref link.Gaps[(int)direction];
            if (gap.Ops > 0)
            {
                if (_deferredMarkers == 0 && TryReserve(RecordHeaderSize, out var gapAt))
                {
                    WriteGapRecord(gapAt, link.SessionId, direction, gap);
                    gap = default;
                }
                else
                {
                    gap.Add(length, ticks);
                    return ValueTask.CompletedTask;
                }
            }

            if (_deferredMarkers == 0 && _blocked == 0 && size <= _ring.Length && TryReserve((int)size, out var at))
            {
                WriteOpRecord(at, link.SessionId, direction, opId, ticks, data, redactionStart, redactionLength);
            }
            else if (!_block)
            {
                gap.Add(length, ticks);
            }
            else
            {
                var pending = link.Pending[(int)direction];
                pending.Active = true;
                pending.Direct = false;
                pending.Data = data;
                pending.OpId = opId;
                pending.Ticks = ticks;
                pending.RedactionStart = redactionStart;
                pending.RedactionLength = redactionLength;
                _blocked++;
                wait = pending.Done.Arm();
            }

            wake = TakeWakeLocked();
        }

        if (wake)
            _wake.Set();
        return wait;
    }

    /// <summary>The session closed: queues its close block (after any gap still owed) and releases its held producers.</summary>
    public void EnqueueClose(SinkLink link, DisconnectReason reason, long ticks)
    {
        List<PendingOp>? release = null;
        bool wake;
        lock (_gate)
        {
            if (_stopping || !link.Open)
                return;

            link.Open = false;
            _links.Remove(link);
            var close = new ControlEvent(ControlKind.Close, link)
            {
                Close = new NntlSessionClose(link.SessionId, ticks, NntlCloseKind.Disconnected, reason),
                InboundGap = link.Gaps[0],
                OutboundGap = link.Gaps[1],
            };
            link.Gaps[0] = default;
            link.Gaps[1] = default;
            ReleasePendingLocked(link, ref release);
            EnqueueControlLocked(close);
            wake = TakeWakeLocked();
        }

        Signal(release);
        if (wake)
            _wake.Set();
    }

    /// <summary>Stops the sink: no more ops are taken, what the ring holds is written, then each open session gets a close block (recording stopped) and the stream ends.</summary>
    public void Stop()
    {
        SinkLink[] links;
        bool wake;
        lock (_gate)
        {
            if (_stopping)
                return;
            _stopping = true;
            links = _links.ToArray();
            wake = TakeWakeLocked();
        }

        foreach (var link in links)
            link.Session.RemoveLink(link);
        _inspector.Forget(this);

        // Unblocks a flush waiting on a viewer that stopped reading; the final flush has its own timeout.
        _stop.Cancel();
        if (wake)
            _wake.Set();
    }

    private async Task RunAsync(InitialSession[] initial)
    {
        try
        {
            _writer = new NntlWriter(_output.Stream,
                NntlFileHeader.Create(NexusInspector.Producer, NexusInspector.ProducerVersion, Environment.MachineName),
                leaveOpen: true);
            if (!_inspector.SchemaHash.IsEmpty)
                _writer.WriteSchema(_inspector.SchemaHash.Span, _inspector.Schema.Span);
            foreach (var session in initial)
            {
                _writer.WriteSessionOpen(session.Open);
                if (session.Snapshot != null)
                    _writer.WriteSnapshot(session.Open.SessionId, session.SnapshotTicks, session.Snapshot);
            }

            var alive = await _output.FlushAsync(idle: false, _stop.Token).ConfigureAwait(false);
            var finished = false;
            while (alive && !finished)
            {
                var wait = ValueTask.CompletedTask;
                var waiting = false;
                var exit = false;
                PendingOp? direct;
                long tail, head;
                lock (_gate)
                {
                    direct = AdmitLocked();
                    tail = _tail;
                    head = _head;
                    if (tail == head && direct == null && _released.Count == 0)
                    {
                        if (_stopping)
                        {
                            exit = true;
                        }
                        else
                        {
                            _senderWaiting = true;
                            wait = _wake.Arm();
                            waiting = true;
                        }
                    }
                }

                ReleaseAdmitted();
                if (direct != null)
                    WriteDirect(direct);
                if (exit)
                    break;

                if (waiting)
                {
                    alive = await _output.FlushAsync(idle: true, _stop.Token).ConfigureAwait(false);
                    await wait.ConfigureAwait(false);
                    continue;
                }

                if (tail != head)
                {
                    finished = Process(tail, head);
                    lock (_gate)
                        _tail = head;
                }
                alive = await _output.FlushAsync(idle: false, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            Fault = e;
        }
        finally
        {
            await FinishAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Places what waits for room, in order: deferred markers, held Block-mode ops, then owed gaps. Sender only.</summary>
    private PendingOp? AdmitLocked()
    {
        if (_stopping)
        {
            // Producers held by a stopping sink are let go; their ops are not part of the stream any more.
            List<PendingOp>? released = _released;
            foreach (var link in _links)
                ReleasePendingLocked(link, ref released);
            return null;
        }

        while (_deferredMarkers > 0 && TryReserve(RecordHeaderSize, out var at))
        {
            WriteHeader(at, RecordKind.Control, 0, 0, 0, 0, 0, 0, 0);
            _deferredMarkers--;
        }

        if (_deferredMarkers > 0)
            return null;

        PendingOp? direct = null;
        if (_blocked > 0)
        {
            foreach (var link in _links)
            {
                for (var d = 0; d < 2; d++)
                {
                    var p = link.Pending[d];
                    if (!p.Active || p.Direct)
                        continue;
                    var size = RecordSize(p.Data.Length);
                    if (size <= _ring.Length)
                    {
                        if (!TryReserve((int)size, out var at))
                            return direct;
                        WriteOpRecord(at, link.SessionId, (TapDirection)d, p.OpId, p.Ticks, p.Data, p.RedactionStart,
                            p.RedactionLength);
                        p.Active = false;
                        p.Data = default;
                        _blocked--;
                        _released.Add(p);
                    }
                    else if (direct == null && _head == _tail)
                    {
                        p.Direct = true;
                        direct = p;
                    }
                }
            }
        }

        if (!_block)
        {
            foreach (var link in _links)
            {
                for (var d = 0; d < 2; d++)
                {
                    ref var gap = ref link.Gaps[d];
                    if (gap.Ops == 0)
                        continue;
                    if (!TryReserve(RecordHeaderSize, out var at))
                        return direct;
                    WriteGapRecord(at, link.SessionId, (TapDirection)d, gap);
                    gap = default;
                }
            }
        }

        return direct;
    }

    private void ReleaseAdmitted()
    {
        foreach (var p in _released)
            p.Done.Set();
        _released.Clear();
    }

    /// <summary>Writes an op larger than the ring straight from the producer's borrowed bytes.</summary>
    private void WriteDirect(PendingOp p)
    {
        var data = p.Data;
        ReadOnlySpan<byte> bytes;
        if (data.IsSingleSegment)
        {
            bytes = data.FirstSpan;
        }
        else
        {
            if (_scratch.Length < data.Length)
                _scratch = new byte[data.Length];
            data.CopyTo(_scratch);
            bytes = _scratch.AsSpan(0, (int)data.Length);
        }

        var link = FindLink(p);
        if (link != null)
            WriteOp(link.SessionId, link.Pending[0] == p ? TapDirection.Inbound : TapDirection.Outbound, p.OpId, p.Ticks,
                bytes, p.RedactionStart, p.RedactionLength);

        lock (_gate)
        {
            if (p.Active)
            {
                p.Active = false;
                p.Direct = false;
                p.Data = default;
                _blocked--;
            }
        }

        p.Done.Set();
    }

    private SinkLink? FindLink(PendingOp p)
    {
        lock (_gate)
        {
            foreach (var link in _links)
            {
                if (link.Pending[0] == p || link.Pending[1] == p)
                    return link;
            }
        }

        return null;
    }

    /// <summary>Writes the committed records in [tail, head) as blocks. Returns true when a single-session stream's session closed.</summary>
    private bool Process(long tail, long head)
    {
        var closed = false;
        var p = tail;
        while (p < head)
        {
            var at = (int)(p % _ring.Length);
            var kind = (RecordKind)_ring[at];
            if (kind == RecordKind.Wrap)
            {
                p += _ring.Length - at;
                continue;
            }

            var record = _ring.AsSpan(at, RecordHeaderSize);
            var direction = (TapDirection)record[1];
            var dataLength = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(4));
            var sessionId = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(8));
            var a = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(16));
            var b = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(24));
            var c = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(32));
            var d = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(40));
            switch (kind)
            {
                case RecordKind.Op:
                    WriteOp(sessionId, direction, a, b, _ring.AsSpan(at + RecordHeaderSize, dataLength), (int)(c >> 32),
                        (int)c);
                    break;
                case RecordKind.Gap:
                    _writer!.WriteGap(new NntlGap(sessionId, direction, b, c, a, d));
                    break;
                case RecordKind.Control:
                    ControlEvent control;
                    lock (_gate)
                        control = _controls.Dequeue();
                    closed |= WriteControl(control);
                    break;
            }

            p += RecordSize(dataLength);
        }

        return closed;
    }

    private void WriteOp(long sessionId, TapDirection direction, long opId, long ticks, ReadOnlySpan<byte> bytes,
        int redactionStart, int redactionLength)
    {
        if (redactionStart >= 0)
        {
            _range[0] = new NntlByteRange(redactionStart, redactionLength);
            _writer!.WriteTransportOp(sessionId, direction, opId, ticks, bytes, _range);
        }
        else
        {
            _writer!.WriteTransportOp(sessionId, direction, opId, ticks, bytes);
        }
    }

    private bool WriteControl(ControlEvent control)
    {
        if (control.Kind == ControlKind.Open)
        {
            _writer!.WriteSessionOpen(control.Open);
            if (control.Snapshot != null)
                _writer.WriteSnapshot(control.Open.SessionId, control.SnapshotTicks, control.Snapshot);
            return false;
        }

        WriteOwedGaps(control.Link.SessionId, control.InboundGap, control.OutboundGap);
        _writer!.WriteSessionClose(control.Close);
        return _single;
    }

    private void WriteOwedGaps(long sessionId, in GapState inbound, in GapState outbound)
    {
        if (inbound.Ops > 0)
            _writer!.WriteGap(new NntlGap(sessionId, TapDirection.Inbound, inbound.FromTicks, inbound.ToTicks, inbound.Ops, inbound.Bytes));
        if (outbound.Ops > 0)
            _writer!.WriteGap(new NntlGap(sessionId, TapDirection.Outbound, outbound.FromTicks, outbound.ToTicks, outbound.Ops, outbound.Bytes));
    }

    /// <summary>Ends the stream: queued closes, a recording-stopped close for every session still open, the footer.</summary>
    private async Task FinishAsync()
    {
        ControlEvent[] remaining;
        List<(SinkLink Link, GapState Inbound, GapState Outbound)> open = [];
        List<PendingOp>? release = null;
        SinkLink[] links;
        lock (_gate)
        {
            _stopping = true;
            remaining = _controls.ToArray();
            _controls.Clear();
            _deferredMarkers = 0;
            links = _links.ToArray();
            foreach (var link in links)
            {
                if (link.Open)
                    open.Add((link, link.Gaps[0], link.Gaps[1]));
                link.Open = false;
                ReleasePendingLocked(link, ref release);
            }

            _links.Clear();
        }

        Signal(release);
        foreach (var link in links)
            link.Session.RemoveLink(link);
        _inspector.Forget(this);

        if (_writer != null && Fault == null)
        {
            try
            {
                foreach (var control in remaining)
                    WriteControl(control);
                var ticks = Stopwatch.GetTimestamp();
                foreach (var (link, inbound, outbound) in open)
                {
                    WriteOwedGaps(link.SessionId, inbound, outbound);
                    _writer.WriteSessionClose(new NntlSessionClose(link.SessionId, ticks, NntlCloseKind.RecordingStopped,
                        DisconnectReason.None));
                }

                _writer.Close();
                using var timeout = new CancellationTokenSource(FinalFlushTimeout);
                await _output.FlushAsync(idle: true, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Fault ??= e;
            }
        }

        try
        {
            _writer?.Dispose();
        }
        catch (Exception e)
        {
            Fault ??= e;
        }

        try
        {
            await _output.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Fault ??= e;
        }

        _completion.TrySetResult();
    }

    private void ReleasePendingLocked(SinkLink link, ref List<PendingOp>? release)
    {
        foreach (var p in link.Pending)
        {
            if (!p.Active)
                continue;
            p.Active = false;
            p.Direct = false;
            p.Data = default;
            _blocked--;
            (release ??= []).Add(p);
        }
    }

    private static void Signal(List<PendingOp>? release)
    {
        if (release == null)
            return;
        foreach (var p in release)
            p.Done.Set();
    }

    private void EnqueueControlLocked(ControlEvent control)
    {
        _controls.Enqueue(control);
        if (_deferredMarkers == 0 && TryReserve(RecordHeaderSize, out var at))
            WriteHeader(at, RecordKind.Control, 0, 0, 0, 0, 0, 0, 0);
        else
            _deferredMarkers++;
    }

    private bool TakeWakeLocked()
    {
        if (!_senderWaiting)
            return false;
        _senderWaiting = false;
        return true;
    }

    private static long RecordSize(long dataLength) => RecordHeaderSize + ((dataLength + 7) & ~7L);

    /// <summary>Reserves a contiguous record of <paramref name="size"/> bytes (a multiple of 8), wrapping to the start when the end is too short.</summary>
    private bool TryReserve(int size, out int at)
    {
        var capacity = _ring.Length;
        if (_head == _tail && _head % capacity != 0)
        {
            // Empty: start the next record at offset 0, so any record up to the whole ring fits. The sender only
            // writes _tail after a batch it saw non-empty, so it never holds a stale position across this.
            _head = _tail = (_head / capacity + 1) * capacity;
        }

        var free = capacity - (int)(_head - _tail);
        at = (int)(_head % capacity);
        var toEnd = capacity - at;
        if (size <= toEnd)
        {
            if (size > free)
                return false;
            _head += size;
            return true;
        }

        if (toEnd + size > free)
            return false;

        _ring[at] = (byte)RecordKind.Wrap;
        _head += toEnd + size;
        at = 0;
        return true;
    }

    private void WriteOpRecord(int at, long sessionId, TapDirection direction, long opId, long ticks,
        in ReadOnlySequence<byte> data, int redactionStart, int redactionLength)
    {
        WriteHeader(at, RecordKind.Op, direction, (int)data.Length, sessionId, opId, ticks,
            ((long)redactionStart << 32) | (uint)redactionLength, 0);
        data.CopyTo(_ring.AsSpan(at + RecordHeaderSize));
    }

    private void WriteGapRecord(int at, long sessionId, TapDirection direction, in GapState gap) =>
        WriteHeader(at, RecordKind.Gap, direction, 0, sessionId, gap.Ops, gap.FromTicks, gap.ToTicks, gap.Bytes);

    private void WriteHeader(int at, RecordKind kind, TapDirection direction, int dataLength, long sessionId, long a,
        long b, long c, long d)
    {
        var record = _ring.AsSpan(at, RecordHeaderSize);
        record[0] = (byte)kind;
        record[1] = (byte)direction;
        record[2] = 0;
        record[3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(record.Slice(4), dataLength);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(8), sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(16), a);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(24), b);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(32), c);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(40), d);
    }
}
