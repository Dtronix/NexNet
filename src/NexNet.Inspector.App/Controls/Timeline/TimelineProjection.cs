using System.Globalization;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.Timeline;

/// <summary>
/// Projects a <see cref="CaptureSession"/> (I18's document model) onto a <see cref="TimelineModel"/>, the lanes of the
/// inspect screens: TRANSPORT reads/writes (one block per op, transport split marks), FRAMES inbound/outbound,
/// HANDSHAKE (preamble and greetings), INVOCATIONS (spans, Gantt-packed), PIPES · CHANNELS and COLLECTIONS (a lane per
/// pipe for its lifetime, with open and close marks) and SESSION CONTROL (ping, credit, window, cancel, disconnect).
/// </summary>
/// <remarks>
/// Incremental: ops, frames and pipe events are appended from where the last update stopped, so a live capture's
/// refresh costs what arrived since; invocations (whose ends move while they are pending) are re-packed each update.
/// Call it on the UI thread; it reads the session under its gate.
/// </remarks>
public sealed class TimelineProjection
{
    private readonly TimelineModel _model;
    private readonly long[] _lastOp = [-1, -1];
    private readonly int[] _frames = new int[2];
    private readonly List<int> _invocationLabels = [];
    private readonly List<bool> _invocationNamed = [];
    private readonly Dictionary<int, PipeState> _pipes = [];
    private CaptureSession? _session;
    private int _pipeEvents;
    private string? _transport;

    /// <param name="model">The model to fill.</param>
    public TimelineProjection(TimelineModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    /// <summary>The session projected; null before the first update or after a reset.</summary>
    public CaptureSession? Session => _session;

    /// <summary>Brings the model up to date with <paramref name="session"/> and notifies its views.</summary>
    public void Update(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session, _session))
        {
            Reset();
            _session = session;
            AddFixedLanes();
        }

        lock (session.Gate)
        {
            _model.Frequency = session.Header.StopwatchFrequency;
            _model.Bounds = new TimeWindow(session.StartTicks, Math.Max(session.EndTicks, session.StartTicks + 1));
            if (_transport is null || !string.Equals(_transport, session.Open.TransportName, StringComparison.Ordinal))
            {
                _transport = session.Open.TransportName;
                _model.TransportName = _transport.ToUpperInvariant();
            }

            AddOps(session, TapDirection.Inbound);
            AddOps(session, TapDirection.Outbound);
            UpdatePipes(session);
            AddFrames(session, TapDirection.Inbound);
            AddFrames(session, TapDirection.Outbound);
            AddPipeEvents(session);
            UpdateInvocations(session);
            _model.ClearGaps();
            foreach (ref readonly var gap in session.Gaps)
                _model.AddGap(new TimelineGap(gap.FromTicks, Math.Max(gap.FromTicks, gap.ToTicks), gap.Direction, gap.DroppedOps, gap.DroppedBytes));
        }

        _model.NotifyChanged();
    }

    /// <summary>Empties the model (a detach, another document).</summary>
    public void Reset()
    {
        _session = null;
        _lastOp[0] = _lastOp[1] = -1;
        _frames[0] = _frames[1] = 0;
        _pipeEvents = 0;
        _transport = null;
        _invocationLabels.Clear();
        _invocationNamed.Clear();
        _pipes.Clear();
        _model.Clear();
        _model.NotifyChanged();
    }

    private void AddFixedLanes()
    {
        _model.AlwaysShow(LaneGroupKind.Transport);
        _model.AlwaysShow(LaneGroupKind.Frames);
        _model.AlwaysShow(LaneGroupKind.Invocations);
        _model.AlwaysShow(LaneGroupKind.Control);
        _model.GetOrAddLane(new LaneKey(LaneKind.Reads, 0), LaneGroupKind.Transport, LaneGlyph.Inbound, "reads");
        _model.GetOrAddLane(new LaneKey(LaneKind.Writes, 0), LaneGroupKind.Transport, LaneGlyph.Outbound, "writes");
        _model.GetOrAddLane(new LaneKey(LaneKind.InboundFrames, 0), LaneGroupKind.Frames, LaneGlyph.Inbound, "inbound");
        _model.GetOrAddLane(new LaneKey(LaneKind.OutboundFrames, 0), LaneGroupKind.Frames, LaneGlyph.Outbound, "outbound");
        _model.GetOrAddLane(new LaneKey(LaneKind.Control, 0), LaneGroupKind.Control, LaneGlyph.Control, "Control").SubLabel =
            "ping · credit · window · cancel";
    }

    private void AddOps(CaptureSession session, TapDirection direction)
    {
        ReadOnlySpan<StreamOp> ops = session.GetStore(direction).Ops;
        int d = (int)direction;

        // A live store drops its oldest ops: find the first op not yet projected by id, not by position.
        int lo = 0, hi = ops.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (ops[mid].OpId <= _lastOp[d]) lo = mid + 1;
            else hi = mid;
        }

        if (lo == ops.Length) return;
        TimelineLane lane = _model.FindLane(new LaneKey(direction == TapDirection.Inbound ? LaneKind.Reads : LaneKind.Writes, 0))!;
        for (int i = lo; i < ops.Length; i++)
            lane.Add(TimelineMark.Op(ops[i].Ticks, direction, ops[i].Length));
        _lastOp[d] = ops[^1].OpId;
    }

    private void AddFrames(CaptureSession session, TapDirection direction)
    {
        ReadOnlySpan<FrameRecord> frames = session.Assembler.GetFrames(direction);
        int d = (int)direction;
        if (_frames[d] >= frames.Length) return;
        TimelineLane lane = _model.FindLane(new LaneKey(direction == TapDirection.Inbound ? LaneKind.InboundFrames : LaneKind.OutboundFrames, 0))!;
        TimelineLane transport = _model.FindLane(new LaneKey(direction == TapDirection.Inbound ? LaneKind.Reads : LaneKind.Writes, 0))!;
        TimelineLane control = _model.FindLane(new LaneKey(LaneKind.Control, 0))!;
        for (int i = _frames[d]; i < frames.Length; i++)
        {
            ref readonly FrameRecord frame = ref frames[i];
            FrameLink link = session.Assembler.GetLink(direction, frame.Seq);
            MarkFlags flags = (frame.Flags & FrameFlags.Cancel) != 0 ? MarkFlags.Cancelled : MarkFlags.None;
            TimelineMark mark = TimelineMark.Frame(frame.StartTicks, frame.EndTicks, direction, frame.Seq, frame.Length,
                _model.BytesLabel(frame.Length), link.Entity, link.EntityIndex, flags);
            lane.Add(mark);
            if ((frame.Flags & FrameFlags.TransportSplit) != 0)
                transport.Add(TimelineMark.Line(MarkKind.TransportSplit, frame.EndTicks, frame.EndTicks, direction));
            Route(session, frame, mark, control);
        }

        _frames[d] = frames.Length;
    }

    private void Route(CaptureSession session, in FrameRecord frame, in TimelineMark mark, TimelineLane control)
    {
        if (frame.Kind == FrameKind.Preamble
            || frame.Type is MessageType.ClientGreeting or MessageType.ClientGreetingReconnection or MessageType.ServerGreeting)
        {
            TimelineLane handshake = _model.GetOrAddLane(new LaneKey(LaneKind.Session, 0), LaneGroupKind.Handshake, LaneGlyph.Session, "Session");
            handshake.SubLabel ??= "preamble · greetings";
            handshake.Add(mark);
            return;
        }

        switch (frame.Type)
        {
            case MessageType.Ping or MessageType.InvocationCredit or MessageType.PipeWindowUpdate or MessageType.InvocationCancellation:
            case >= MessageType.DisconnectSocketError and < MessageType.DuplexPipeWrite:
                control.Add(mark);
                return;
            case MessageType.DuplexPipeWrite when mark.Entity == EntityKind.Pipe && mark.EntityIndex >= 0:
            {
                if (!_pipes.TryGetValue(mark.EntityIndex, out PipeState? pipe)) pipe = AddPipe(session, mark.EntityIndex);
                pipe.Lane.Add(mark);
                if (pipe.FirstTicks > frame.StartTicks) pipe.FirstTicks = frame.StartTicks;
                if ((frame.Flags & FrameFlags.NexNetSplit) != 0)
                    pipe.Lane.Add(TimelineMark.Line(MarkKind.NexNetSplit, frame.StartTicks, frame.StartTicks, frame.Direction));
                UpdateLifetime(session, pipe, session.Assembler.Pipes[mark.EntityIndex]);
                return;
            }
        }
    }

    private void UpdatePipes(CaptureSession session)
    {
        ReadOnlySpan<PipeRow> pipes = session.Assembler.Pipes;
        for (int i = 0; i < pipes.Length; i++)
        {
            if (!_pipes.TryGetValue(i, out PipeState? pipe)) pipe = AddPipe(session, i);
            ref readonly PipeRow row = ref pipes[i];
            int count = row.Collection >= 0 ? session.Assembler.Collections[row.Collection].MessageCount : 0;
            bool named = row.OpenerInvocation >= 0;
            if (pipe.Content != row.Content || pipe.Lifecycle != row.Lifecycle || pipe.Named != named || pipe.Messages != count)
            {
                pipe.Content = row.Content;
                pipe.Lifecycle = row.Lifecycle;
                pipe.Named = named;
                pipe.Messages = count;
                Describe(session, pipe, row);
            }

            UpdateLifetime(session, pipe, row);
        }
    }

    private PipeState AddPipe(CaptureSession session, int index)
    {
        PipeRow row = session.Assembler.Pipes[index];
        TimelineLane lane = _model.GetOrAddLane(new LaneKey(LaneKind.Pipe, index), LaneGroupKind.Pipes, LaneGlyph.Pipe, "Pipe",
            LaneVisibility.Lifetime);
        PipeState pipe = new(lane) { Content = row.Content, Lifecycle = row.Lifecycle, Named = row.OpenerInvocation >= 0 };
        _pipes.Add(index, pipe);
        Describe(session, pipe, row);
        UpdateLifetime(session, pipe, row);
        return pipe;
    }

    private void Describe(CaptureSession session, PipeState pipe, in PipeRow row)
    {
        TimelineLane lane = pipe.Lane;
        string ids = (row.PipeId & 0xFF).ToString(CultureInfo.InvariantCulture) + "·" + (row.PipeId >> 8).ToString(CultureInfo.InvariantCulture);
        string? method = null;
        if (row.OpenerInvocation >= 0 && row.OpenerInvocation < session.Assembler.Invocations.Length
            && session.Decoder.TryGetMethod(session.Assembler.Invocations[row.OpenerInvocation], out SchemaMethod? m))
        {
            method = m.Name;
        }

        if (row.Content == PipeContent.Collection)
        {
            lane.Group = LaneGroupKind.Collections;
            lane.Glyph = LaneGlyph.Collection;
            ushort id = row.Collection >= 0 ? session.Assembler.Collections[row.Collection].CollectionId : (ushort)0;
            lane.Label = row.Collection >= 0 && session.Decoder.TryGetCollection(id, out SchemaCollection? collection)
                ? collection.Name
                : "Collection " + ids;
            lane.SubLabel = pipe.Messages.ToString("N0", CultureInfo.InvariantCulture) + (pipe.Messages == 1 ? " message" : " messages")
                + State(row.Lifecycle);
            return;
        }

        lane.Group = LaneGroupKind.Pipes;
        lane.Glyph = LaneGlyph.Pipe;
        lane.Label = (row.Content == PipeContent.Channel ? "Chan " : "Pipe ") + ids;
        string state = row.Lifecycle switch
        {
            PipeLifecycle.Closed => "closed",
            PipeLifecycle.Refused => "refused",
            PipeLifecycle.Closing => "closing",
            _ => row.Content == PipeContent.Raw ? "raw" : "open",
        };
        lane.SubLabel = method is null ? state : method + " · " + state;
    }

    private static string State(PipeLifecycle lifecycle) => lifecycle switch
    {
        PipeLifecycle.Closed => " · closed",
        PipeLifecycle.Refused => " · refused",
        _ => "",
    };

    private void UpdateLifetime(CaptureSession session, PipeState pipe, in PipeRow row)
    {
        TimelineLane lane = pipe.Lane;
        lane.LifeStart = pipe.OpenTicks != long.MaxValue ? pipe.OpenTicks
            : row.FromSnapshot || row.OpenSeq < 0 ? Math.Min(session.StartTicks, pipe.FirstTicks)
            : pipe.FirstTicks;
        lane.LifeEnd = row.Lifecycle is PipeLifecycle.Closed or PipeLifecycle.Refused && pipe.CloseTicks != long.MinValue
            ? pipe.CloseTicks
            : long.MaxValue;
    }

    private void AddPipeEvents(CaptureSession session)
    {
        ReadOnlySpan<PipeEventRow> events = session.Assembler.PipeEvents;
        for (int i = _pipeEvents; i < events.Length; i++)
        {
            ref readonly PipeEventRow e = ref events[i];
            if (e.Pipe < 0 || !session.TryGetFrame(new FrameKey(e.Direction, e.Seq), out FrameRecord frame)) continue;
            if (!_pipes.TryGetValue(e.Pipe, out PipeState? pipe)) pipe = AddPipe(session, e.Pipe);
            MarkKind kind;
            if (e.Kind == PipeEventKind.Ready)
            {
                kind = MarkKind.Open;
                pipe.OpenTicks = Math.Min(pipe.OpenTicks, frame.StartTicks);
            }
            else if (e.Kind == PipeEventKind.Refused || (e.Kind == PipeEventKind.State && (e.State & PipeStateFlags.Complete) == PipeStateFlags.Complete))
            {
                kind = MarkKind.Close;
                pipe.CloseTicks = Math.Max(pipe.CloseTicks, frame.StartTicks);
            }
            else
            {
                continue;
            }

            pipe.Lane.Add(new TimelineMark(frame.StartTicks, frame.StartTicks, kind, e.Direction, MarkFlags.None, EntityKind.Pipe,
                e.Pipe, e.Seq, frame.Length, -1));
            UpdateLifetime(session, pipe, session.Assembler.Pipes[e.Pipe]);
        }

        _pipeEvents = events.Length;
    }

    private void UpdateInvocations(CaptureSession session)
    {
        ReadOnlySpan<InvocationRow> invocations = session.Assembler.Invocations;
        _model.ClearInvocations();
        for (int i = 0; i < invocations.Length; i++)
        {
            ref readonly InvocationRow row = ref invocations[i];
            long start = row.HasRequest && TryFirstTicks(session, row.Request, out long first) ? first : session.StartTicks;
            MarkFlags flags = row.State == InvocationState.Canceled ? MarkFlags.Cancelled : MarkFlags.None;
            long end;
            if (!row.Result.IsNone && TryLastTicks(session, row.Result, out long last))
                end = last;
            else if (row.State == InvocationState.OneWay && TryLastTicks(session, row.Request, out long sent))
                end = sent;
            else if (row.State == InvocationState.Pending)
            {
                end = session.EndTicks;
                flags |= MarkFlags.Pending;
            }
            else
                end = row.CancellationSeq >= 0 && session.TryGetFrame(new FrameKey(row.Direction, row.CancellationSeq), out FrameRecord cancel)
                    ? cancel.EndTicks
                    : start;

            _model.AddInvocation(new InvocationSpan(i, start, Math.Max(start, end), Label(session, i, row), flags, row.Direction));
        }

        _model.PackInvocations();
    }

    private int Label(CaptureSession session, int index, in InvocationRow row)
    {
        if (index == _invocationLabels.Count)
        {
            _invocationLabels.Add(_model.AddLabel(Name(session, row, out bool named)));
            _invocationNamed.Add(named);
        }
        else if (!_invocationNamed[index] && row.RequestParsed)
        {
            string text = Name(session, row, out bool named);
            if (named)
            {
                _model.SetLabel(_invocationLabels[index], text);
                _invocationNamed[index] = true;
            }
        }

        return _invocationLabels[index];
    }

    private static string Name(CaptureSession session, in InvocationRow row, out bool named)
    {
        string id = "#" + row.InvocationId.ToString(CultureInfo.InvariantCulture);
        named = row.RequestParsed && session.Decoder.TryGetMethod(row, out _);
        return named && session.Decoder.TryGetMethod(row, out SchemaMethod? method) ? id + " " + method.Name : id;
    }

    private static bool TryFirstTicks(CaptureSession session, in MessageRef message, out long ticks)
    {
        long seq = message.Seq;
        if (message.Kind == MessageRefKind.Assembled && message.Index >= 0)
            seq = session.Assembler.Messages[message.Index].FirstSeq;
        if (seq >= 0 && session.TryGetFrame(new FrameKey(message.Direction, seq), out FrameRecord frame))
        {
            ticks = frame.StartTicks;
            return true;
        }

        ticks = 0;
        return false;
    }

    private static bool TryLastTicks(CaptureSession session, in MessageRef message, out long ticks)
    {
        long seq = message.Seq;
        if (message.Kind == MessageRefKind.Assembled && message.Index >= 0)
            seq = session.Assembler.Messages[message.Index].LastSeq;
        if (seq >= 0 && session.TryGetFrame(new FrameKey(message.Direction, seq), out FrameRecord frame))
        {
            ticks = frame.EndTicks;
            return true;
        }

        ticks = 0;
        return false;
    }

    private sealed class PipeState(TimelineLane lane)
    {
        public TimelineLane Lane { get; } = lane;

        public PipeContent Content { get; set; }

        public PipeLifecycle Lifecycle { get; set; }

        public bool Named { get; set; }

        public int Messages { get; set; }

        public long OpenTicks { get; set; } = long.MaxValue;

        public long CloseTicks { get; set; } = long.MinValue;

        public long FirstTicks { get; set; } = long.MaxValue;
    }
}
