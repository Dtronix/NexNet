using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.DataTable.Events;

/// <summary>
/// The Events table's rows: an <see cref="System.Collections.IList"/> adapter over a <see cref="CaptureSession"/>'s frame
/// index (<see cref="CaptureSession.FrameOrder"/>, every frame of both directions in capture order). It copies nothing
/// per frame but one int per frame for the reverse lookup (frame → row, for selection from other views); a row is read
/// from the session under its gate only when a container shows it.
/// </summary>
/// <remarks>UI thread only. <see cref="Refresh"/> after the session changed (the document's DataChanged).</remarks>
public sealed class EventsTableSource : DataTableSource
{
    private readonly List<int>[] _rowBySeq = [[], []];
    private readonly long[] _firstSeq = [-1, -1];
    private readonly List<TraceFragment> _fragments = [];
    private CaptureSession? _session;
    private int _count;
    private long _version = -1;
    private MessageRef _trace = MessageRef.None;
    private MessageTrace? _members;

    /// <summary>The session shown; null shows no rows.</summary>
    public CaptureSession? Session => _session;

    public override int Count => _count;

    /// <summary>
    /// The traced message: its frames get the trace tint. Set from the selection for now (the selected frame's message);
    /// I25's trace owns it later.
    /// </summary>
    public MessageRef Trace
    {
        get => _trace;
        set
        {
            if (value == _trace) return;
            _trace = value;
            NotifyRowsChanged();
        }
    }

    /// <summary>
    /// The trace resolved on the sync bus: when set (and active), the rows of every frame that carried part of it are
    /// tinted (an invocation's request, cancellation and result; the handshake's four frames), in place of
    /// <see cref="Trace"/>'s single message.
    /// </summary>
    public MessageTrace? Members
    {
        get => _members;
        set
        {
            if (ReferenceEquals(value, _members)) return;
            _members = value;
            NotifyRowsChanged();
        }
    }

    /// <summary>Shows <paramref name="session"/> (null: nothing), replacing every row.</summary>
    public void Attach(CaptureSession? session)
    {
        if (ReferenceEquals(session, _session)) return;
        _session = session;
        _count = 0;
        _version = -1;
        _trace = MessageRef.None;
        _rowBySeq[0].Clear();
        _rowBySeq[1].Clear();
        _firstSeq[0] = _firstSeq[1] = -1;
        NotifyReset();
        Refresh();
    }

    /// <summary>Takes in the frames added since the last refresh; returns how many rows were added.</summary>
    public int Refresh()
    {
        if (_session is not { } session) return 0;
        long version = session.Version;
        if (version == _version) return 0;
        _version = version;

        int start = _count;
        lock (session.Gate)
        {
            ReadOnlySpan<FrameKey> order = session.FrameOrder;
            for (int i = start; i < order.Length; i++)
                Remember(order[i], i);
            _count = order.Length;
        }

        NotifyAdded(start, _count - start);
        // Frames already shown gain flags as assembly goes on (N, X), and an incomplete one completes.
        NotifyRowsChanged();
        return _count - start;
    }

    /// <summary>The row of <paramref name="frame"/>; -1 when the session has no such frame.</summary>
    public int IndexOf(FrameKey frame)
    {
        int d = (int)frame.Direction;
        long offset = frame.Seq - _firstSeq[d];
        List<int> rows = _rowBySeq[d];
        return _firstSeq[d] >= 0 && offset >= 0 && offset < rows.Count ? rows[(int)offset] : -1;
    }

    /// <summary>The frame shown at <paramref name="index"/>.</summary>
    public bool TryGetFrame(int index, out FrameKey frame)
    {
        frame = default;
        if (_session is not { } session || (uint)index >= (uint)_count) return false;
        lock (session.Gate) frame = session.FrameOrder[index];
        return true;
    }

    /// <summary>Reads the row at <paramref name="index"/> from the session.</summary>
    public bool TryGetRow(int index, out EventRow row)
    {
        row = default;
        if (_session is not { } session || (uint)index >= (uint)_count) return false;
        lock (session.Gate)
            return TryRead(session, index, out row);
    }

    public override DataTableRowVisual CreateVisual() => new EventRowVisual();

    public override void Prepare(DataTableRowVisual visual, int index)
    {
        if (visual is EventRowVisual events && TryGetRow(index, out EventRow row))
            events.Set(index, row);
    }

    private void Remember(FrameKey frame, int index)
    {
        int d = (int)frame.Direction;
        List<int> rows = _rowBySeq[d];
        if (_firstSeq[d] < 0) _firstSeq[d] = frame.Seq;
        long offset = frame.Seq - _firstSeq[d];
        if (offset < 0) return;

        // A direction's frames complete in sequence order; a skipped number (never expected) maps to no row.
        while (rows.Count < offset) rows.Add(-1);
        if (offset == rows.Count) rows.Add(index);
        else rows[(int)offset] = index;
    }

    private bool TryRead(CaptureSession session, int index, out EventRow row)
    {
        row = default;
        FrameKey key = session.FrameOrder[index];
        MessageAssembler assembler = session.Assembler;
        if (!assembler.TryGetFrame(key.Direction, key.Seq, out FrameRecord frame)) return false;

        FrameLink link = assembler.GetLink(key.Direction, key.Seq);
        MessageRef message = assembler.GetMessage(key.Direction, key.Seq);
        long frequency = Math.Max(1, session.Header.StopwatchFrequency);

        EventTargetKind target = EventTargetKind.None;
        ushort targetId = 0;
        string? method = null;
        string? label = null;
        if (link.Entity == EntityKind.Invocation && (uint)link.EntityIndex < (uint)assembler.Invocations.Length)
        {
            InvocationRow invocation = assembler.Invocations[link.EntityIndex];
            target = EventTargetKind.Invocation;
            targetId = invocation.InvocationId;
            if (invocation.RequestParsed && session.Decoder.TryGetMethod(invocation, out SchemaMethod? schemaMethod))
                method = schemaMethod.Name;
        }
        else if (frame.Target == FrameTarget.Invocation)
        {
            target = EventTargetKind.Invocation;
            targetId = frame.TargetId;
        }
        else if (link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length)
        {
            PipeRow pipe = assembler.Pipes[link.EntityIndex];
            target = pipe.Content == PipeContent.Channel ? EventTargetKind.Channel : EventTargetKind.Pipe;
            targetId = pipe.PipeId;
            label = session.Decoder.GetPipeLabel(link.EntityIndex);
        }
        else if (frame.Target == FrameTarget.Pipe)
        {
            target = EventTargetKind.Pipe;
            targetId = frame.TargetId;
        }

        (int chunk, int chunks) = frame.Type == MessageType.MessageChunk && link.Message >= 0
            ? ChunkOf(assembler, link.Message, key)
            : (0, 0);

        row = new EventRow
        {
            Number = index + 1L,
            Frame = key,
            Ticks = frame.StartTicks,
            TimeMs = (frame.StartTicks - session.StartTicks) * 1000.0 / frequency,
            Kind = frame.Kind,
            Type = frame.Type,
            Length = frame.Length,
            Flags = frame.Flags,
            FirstOpId = frame.FirstOpId,
            LastOpId = frame.LastOpId,
            Message = message,
            Target = target,
            TargetId = targetId,
            MethodName = method,
            PipeLabel = label,
            Detail = assembler.GetDetail(key.Direction, key.Seq),
            ChunkNumber = chunk,
            ChunkCount = chunks,
            IsTraced = _members is { IsActive: true } members ? members.Contains(key) : !_trace.IsNone && message == _trace,
        };
        return true;
    }

    // A chunk's number among its message's chunks (MessageStart and MessageAbort carry none), and their count once the
    // message is complete.
    private (int Number, int Count) ChunkOf(MessageAssembler assembler, int message, FrameKey key)
    {
        AssembledMessage assembled = assembler.Messages[message];
        _fragments.Clear();
        assembler.GetFragments(assembled.Ref, _fragments);
        int number = 0, count = 0;
        foreach (TraceFragment fragment in _fragments)
        {
            if (!assembler.TryGetFrame(fragment.Direction, fragment.Seq, out FrameRecord part) || part.Type != MessageType.MessageChunk)
                continue;
            count++;
            if (fragment.Direction == key.Direction && fragment.Seq == key.Seq) number = count;
        }

        return (number, assembled.State == AssemblyState.Complete ? count : 0);
    }
}
