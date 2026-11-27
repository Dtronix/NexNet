using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Findings;

public sealed partial class FindingsEngine
{
    private readonly long _windowTicks;

    // Scan positions.
    private int _frame;
    private int _gap;
    private int _gapBefore;
    private int _invocation;
    private int _message;
    private int _item;
    private int _pipeEvent;
    private readonly List<bool> _judged = [];
    private readonly bool[] _gapSeen = new bool[2];

    // Pings: the first client ping not answered yet.
    private bool _pingOpen;
    private long _pingAt;
    private int _pingFrame;
    private readonly List<double> _rtts = [];

    // Per direction a message travels: the receiver's MaxMessageSize (0 unknown).
    private readonly long[] _limit = new long[2];

    // Invocation credit per caller direction; pipe windows per (pipe, writer direction).
    private readonly CreditState[] _credits = [new(), new()];
    private readonly Dictionary<long, CreditState> _windows = [];
    private readonly List<long> _windowOrder = [];

    // Chunk storms: recent chunk times and their invocations.
    private readonly Queue<(long Ticks, int Invocation)> _chunks = new();
    private bool _inStorm;

    // Slow calls: sorted durations per method, and calls judged at the end.
    private readonly Dictionary<string, List<double>> _durations = new(StringComparer.Ordinal);
    private readonly List<Deferred> _deferred = [];

    private void ScanFrames()
    {
        var order = _session.FrameOrder;
        var assembler = _session.Assembler;
        var marks = _session.GapMarks;
        var gaps = _session.Gaps;
        for (; _frame < order.Length; _frame++)
        {
            while (_gapBefore < marks.Length && _gapBefore < gaps.Length && marks[_gapBefore].FrameIndex <= _frame)
                _gapSeen[(int)gaps[_gapBefore++].Direction] = true;

            var key = order[_frame];
            if (!assembler.TryGetFrame(key.Direction, key.Seq, out var frame) || frame.Kind == FrameKind.Preamble)
                continue;
            if (frame.Kind == FrameKind.Malformed)
            {
                Malformed(frame);
                continue;
            }

            if ((frame.Flags & FrameFlags.Incomplete) != 0)
            {
                TextValues.AppendSize(_sb.Append(TextRenderer.TypeName(frame.Type)).Append(" frame cut off after "), frame.Length)
                    .Append(" (by a gap or the end of the capture)");
                Add(FindingSeverity.Info, FindingKind.ProtocolError, Text(), [TextRenderer.FrameId(_frame)], frame.StartTicks, frame.EndTicks);
                continue;
            }

            var link = assembler.GetLink(frame.Direction, frame.Seq);
            if ((link.Flags & FrameLinkFlags.BodyMalformed) != 0)
            {
                TextValues.AppendSize(_sb.Append(TextRenderer.TypeName(frame.Type)).Append(" body does not decode ("), frame.Length).Append(')');
                Add(FindingSeverity.Error, FindingKind.ProtocolError, Text(), [TextRenderer.FrameId(_frame)], frame.StartTicks, frame.EndTicks);
                continue;
            }

            switch (frame.Type)
            {
                case MessageType.Ping:
                    Ping(frame);
                    break;
                case MessageType.ClientGreeting:
                case MessageType.ClientGreetingReconnection:
                case MessageType.ServerGreeting:
                    Greeting(frame, link);
                    break;
                case MessageType.Invocation:
                case MessageType.MessageStart:
                    CallSent(frame, link);
                    break;
                case MessageType.InvocationCredit:
                    CreditReturned(frame);
                    break;
                case MessageType.MessageChunk:
                    Chunk(frame, link);
                    break;
                case MessageType.DuplexPipeWrite:
                    PipeWrite(frame, link);
                    break;
                case MessageType.PipeWindowUpdate:
                case MessageType.DuplexPipeUpdateState:
                    PipeGrant(frame, link);
                    break;
            }
        }
    }

    private void Malformed(in FrameRecord frame)
    {
        // Bytes after a gap or an attach are read while the parser resynchronises: expected, not the peer's fault.
        var resync = _gapSeen[(int)frame.Direction] || !_session.IsFromInception(frame.Direction);
        TextValues.AppendSize(_sb, frame.Length).Append(resync
            ? " that do not frame, while resynchronising after a gap or attach"
            : " that do not frame as NexNet messages");
        Add(resync ? FindingSeverity.Info : FindingSeverity.Error, FindingKind.ProtocolError, Text(), [TextRenderer.FrameId(_frame)],
            frame.StartTicks, frame.EndTicks);
    }

    private void ScanGaps()
    {
        var gaps = _session.Gaps;
        var marks = _session.GapMarks;
        for (; _gap < gaps.Length; _gap++)
        {
            var gap = gaps[_gap];
            var after = _gap < marks.Length ? marks[_gap].FrameIndex : 0;
            _sb.Append(gap.Direction == TapDirection.Outbound ? "out " : "in ").Append(Invariant, $"{gap.DroppedOps} ops ");
            TextValues.AppendSize(_sb, gap.DroppedBytes).Append(" dropped (Drop) after the cited frame: messages in flight that way are incomplete");
            Add(FindingSeverity.Warning, FindingKind.Gap, Text(), after > 0 ? [TextRenderer.FrameId(after - 1)] : [SessionTag],
                gap.FromTicks, gap.ToTicks);
        }
    }

    // Pings: a client ping and the server's next ping (its pong) make one round trip.
    private void Ping(in FrameRecord frame)
    {
        var clientToServer = _session.Role == NntlSessionRole.Server ? TapDirection.Inbound : TapDirection.Outbound;
        if (frame.Direction == clientToServer)
        {
            if (!_pingOpen)
            {
                _pingOpen = true;
                _pingAt = frame.StartTicks;
                _pingFrame = _frame;
            }

            return;
        }

        if (!_pingOpen)
            return;
        _pingOpen = false;
        var rtt = Span(_pingAt, frame.StartTicks);
        var median = _rtts.Count >= Options.PingMinSamples ? _rtts[_rtts.Count / 2] : -1;
        if (rtt >= Options.PingRttMs || (median >= 0 && rtt >= Math.Max(Options.PingRttFloorMs, Options.PingRttFactor * median)))
        {
            TextValues.AppendDuration(_sb.Append("ping answered after "), rtt);
            if (median >= 0)
                TextValues.AppendDuration(_sb.Append(", median "), median).Append(Invariant, $" over {_rtts.Count} pings");
            Add(FindingSeverity.Warning, FindingKind.PingRtt, Text(), [TextRenderer.FrameId(_pingFrame), TextRenderer.FrameId(_frame)],
                _pingAt, frame.StartTicks);
        }

        Insert(_rtts, rtt);
    }

    private void Greeting(in FrameRecord frame, in FrameLink link)
    {
        var greetings = _session.Assembler.Greetings;
        if (link.Entity != EntityKind.Greeting || (uint)link.EntityIndex >= (uint)greetings.Length)
            return;
        var greeting = greetings[link.EntityIndex];

        // The sender accepts messages up to its MaxMessageSize and lets its peer queue InvocationCredits calls: both are
        // about what travels toward the sender.
        var toSender = Opposite(frame.Direction);
        if (greeting.MaxMessageSize > 0)
            _limit[(int)toSender] = greeting.MaxMessageSize;
        if (greeting.InvocationCredits > 0)
            _credits[(int)toSender].Grant(greeting.InvocationCredits, frame.StartTicks, initial: true);
    }

    private void CallSent(in FrameRecord frame, in FrameLink link)
    {
        var invocations = _session.Assembler.Invocations;
        if (link.Entity != EntityKind.Invocation || (uint)link.EntityIndex >= (uint)invocations.Length)
            return;
        var row = invocations[link.EntityIndex];
        if (!row.HasRequest || row.Request.Direction != frame.Direction || row.Request.Seq != frame.Seq)
            return;
        Use(_credits[(int)frame.Direction], 1, frame, FindingKind.CreditStall, null);
    }

    private void CreditReturned(in FrameRecord frame)
    {
        var detail = _session.Assembler.GetDetail(frame.Direction, frame.Seq);
        if (detail.Kind == FrameDetailKind.Credit)
            _credits[(int)Opposite(frame.Direction)].Grant(detail.Value, frame.StartTicks, initial: false);
    }

    private void PipeWrite(in FrameRecord frame, in FrameLink link)
    {
        if (link.Entity != EntityKind.Pipe || (uint)link.EntityIndex >= (uint)_session.Assembler.Pipes.Length)
            return;
        if (_windows.TryGetValue(WindowKey(link.EntityIndex, frame.Direction), out var state))
            Use(state, frame.BodyLength, frame, FindingKind.WindowStall, link.EntityIndex);
    }

    // A Ready's window or a window update: credit for the writes that travel the other way.
    private void PipeGrant(in FrameRecord frame, in FrameLink link)
    {
        var events = _session.Assembler.PipeEvents;
        for (var i = _pipeEvent; i < events.Length && i < _pipeEvent + 8; i++)
        {
            var e = events[i];
            if (e.Direction != frame.Direction || e.Seq != frame.Seq)
                continue;
            _pipeEvent = i + 1;
            if (e.Kind is not (PipeEventKind.Ready or PipeEventKind.WindowUpdate) || e.Window == 0)
                return;
            var key = WindowKey(e.Pipe, Opposite(frame.Direction));
            if (!_windows.TryGetValue(key, out var state))
            {
                state = new CreditState();
                _windows[key] = state;
                _windowOrder.Add(key);
            }

            state.Grant(e.Window, frame.StartTicks, initial: false);
            return;
        }
    }

    private static long WindowKey(int pipe, TapDirection writer) => ((long)pipe << 1) | (long)writer;

    // Spends credit; a use after credit ran out and came back confirms a stall (the writer or caller had more to send).
    private void Use(CreditState state, long amount, in FrameRecord frame, FindingKind kind, int? pipe)
    {
        if (!state.Known)
            return;
        if (state.Exhausted)
        {
            if (state.Granted)
            {
                var wait = Span(state.ExhaustedAt, state.GrantAt);
                if (wait >= Options.StallMs)
                    Stall(state, wait, frame, kind, pipe);
            }

            state.Exhausted = false;
            state.Granted = false;
        }

        state.Credit -= amount;
        if (state.Credit <= 0)
        {
            state.Exhausted = true;
            state.ExhaustedAt = frame.EndTicks;
        }
    }

    private void Stall(CreditState state, double wait, in FrameRecord frame, FindingKind kind, int? pipe)
    {
        state.Stalls++;
        state.TotalMs += wait;
        state.LongestMs = Math.Max(state.LongestMs, wait);
        if (state.Stalls == 1)
            state.FirstAt = state.ExhaustedAt;
        state.LastAt = state.GrantAt;
        if (state.Stalls > 1)
            return;

        _sb.Append(Arrow(frame.Direction));
        if (pipe is { } p)
        {
            state.Ids = [_ids.PipeId(p)];
            TextValues.AppendDuration(_sb.Append(" writes waited "), wait).Append(" for a window update after using the whole ");
            TextValues.AppendSize(_sb, state.Window).Append(" window");
        }
        else
        {
            state.Ids = [SessionTag];
            TextValues.AppendDuration(_sb.Append(" calls waited "), wait)
                .Append(Invariant, $" for invocation credit after using all {state.Window} the peer allows");
        }

        Add(FindingSeverity.Warning, kind, Text(), [.. state.Ids, TextRenderer.FrameId(_frame)], state.ExhaustedAt, state.GrantAt);
        state.Direction = frame.Direction;
    }

    private void StallTotals()
    {
        Total(_credits[0], FindingKind.CreditStall);
        Total(_credits[1], FindingKind.CreditStall);
        foreach (var key in _windowOrder)
            Total(_windows[key], FindingKind.WindowStall);
    }

    private void Total(CreditState state, FindingKind kind)
    {
        if (state.Stalls < 2 || state.Ids is null)
            return;
        _sb.Append(Arrow(state.Direction)).Append(Invariant, $" stalled {state.Stalls}× waiting for ")
            .Append(kind == FindingKind.WindowStall ? "window updates, " : "invocation credit, ");
        TextValues.AppendDuration(_sb, state.TotalMs).Append(" in total, longest ");
        TextValues.AppendDuration(_sb, state.LongestMs);
        Add(FindingSeverity.Warning, kind, Text(), state.Ids, state.FirstAt, state.LastAt);
    }

    private void Chunk(in FrameRecord frame, in FrameLink link)
    {
        var invocation = link.Entity == EntityKind.Invocation ? link.EntityIndex : -1;
        _chunks.Enqueue((frame.StartTicks, invocation));
        while (_chunks.Peek().Ticks < frame.StartTicks - _windowTicks)
            _chunks.Dequeue();

        if (_inStorm)
        {
            if (_chunks.Count < Options.ChunkStormFrames / 2)
                _inStorm = false;
            return;
        }

        if (_chunks.Count < Options.ChunkStormFrames)
            return;
        _inStorm = true;
        var ids = new List<string>();
        var invocations = _session.Assembler.Invocations;
        foreach (var (_, index) in _chunks)
        {
            if (index < 0 || (uint)index >= (uint)invocations.Length)
                continue;
            var id = _ids.InvocationId(index);
            if (!ids.Contains(id) && ids.Count < 4)
                ids.Add(id);
        }

        var first = _chunks.Peek().Ticks;
        _sb.Append(Invariant, $"{_chunks.Count} MessageChunk frames within ");
        TextValues.AppendDuration(_sb, Span(first, frame.StartTicks));
        Add(FindingSeverity.Info, FindingKind.ChunkStorm, Text(), ids.Count > 0 ? ids.ToArray() : [TextRenderer.FrameId(_frame)],
            first, frame.StartTicks);
    }

    private static void Insert(List<double> sorted, double value)
    {
        var at = sorted.BinarySearch(value);
        sorted.Insert(at < 0 ? ~at : at, value);
    }

    private sealed class CreditState
    {
        public bool Known;
        public long Credit;
        public long Window;
        public bool Exhausted;
        public long ExhaustedAt;
        public bool Granted;
        public long GrantAt;
        public int Stalls;
        public double TotalMs;
        public double LongestMs;
        public long FirstAt;
        public long LastAt;
        public string[]? Ids;
        public TapDirection Direction;

        public void Grant(long amount, long ticks, bool initial)
        {
            if (initial)
            {
                Credit = amount;
                Window = amount;
            }
            else
            {
                if (!Known)
                    Window = amount;
                Credit += amount;
            }

            Known = true;
            if (Exhausted && !Granted && Credit > 0)
            {
                Granted = true;
                GrantAt = ticks;
            }
        }
    }

    private readonly record struct Deferred(int Invocation, string Key, double Duration, long Start, long End);
}
