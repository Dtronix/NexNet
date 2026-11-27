using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Text;

public sealed partial class TextRenderer
{
    // Summary level: a few lines about the whole session (what it holds now), each its own cursor position.
    private void Summary()
    {
        var line = 0;
        var assembler = _session.Assembler;
        var filter = Options.Filter;

        // Totals.
        if (Next(ref line))
        {
            var inbound = _session.Inbound;
            var outbound = _session.Outbound;
            _sb.Append(Invariant, $"frames {_session.FrameCount} ops {_session.OpCount} in ");
            TextValues.AppendSize(_sb, inbound.ByteCount).Append(" out ");
            TextValues.AppendSize(_sb, outbound.ByteCount).Append(" duration ");
            TextValues.AppendDuration(_sb, Span(_session.StartTicks, _session.EndTicks));
            Stat();
        }

        // Handshake.
        if (Next(ref line))
        {
            var greetings = assembler.Greetings;
            if (greetings.Length > 0)
            {
                var at = assembler.TryGetFrame(greetings[^1].Direction, greetings[^1].Seq, out var frame) ? frame : default;
                Handshake(at);
            }
            else
            {
                Emit(NewRecord(TextRecordKind.Handshake, _session.StartTicks, null) with
                {
                    Outcome = "none",
                    Value = _session.IsFromInception(TapDirection.Inbound) || _session.IsFromInception(TapDirection.Outbound) ? null : "(attached mid-stream)",
                });
            }
        }

        // Calls, overall and per method.
        var methods = new Dictionary<string, MethodStats>(StringComparer.Ordinal);
        var totals = new MethodStats(">", "");
        var invocations = assembler.Invocations;
        for (var i = 0; i < invocations.Length; i++)
        {
            var row = invocations[i];
            var start = RequestStart(row);
            if (!Kept(row.Direction, start, filter))
                continue;
            var name = MethodName(row);
            var key = (row.Direction == TapDirection.Outbound ? ">" : "<") + name;
            if (!methods.TryGetValue(key, out var stats))
            {
                stats = new MethodStats(row.Direction == TapDirection.Outbound ? ">" : "<", name);
                methods[key] = stats;
            }

            var end = ResultEnd(row);
            double? duration = end > 0 && start > 0 ? Span(start, end) : null;
            stats.Add(row.State, duration);
            totals.Add(row.State, duration);
        }

        if (Next(ref line))
        {
            _sb.Append(Invariant, $"calls {totals.Count}");
            totals.AppendStates(_sb);
            Stat();
        }

        var sorted = methods.Values.ToList();
        sorted.Sort(static (a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.Direction + a.Name, b.Direction + b.Name));
        foreach (var stats in sorted)
        {
            if (!Next(ref line))
                continue;
            _sb.Append("  ").Append(stats.Direction).Append(stats.Name).Append(Invariant, $" ×{stats.Count}");
            stats.AppendStates(_sb);
            stats.AppendLatency(_sb);
            Stat();
        }

        // Pipes.
        var pipes = assembler.Pipes;
        if (Next(ref line))
        {
            _sb.Append(Invariant, $"pipes {pipes.Length}");
            Stat();
        }

        for (var i = 0; i < pipes.Length; i++)
        {
            if (!Next(ref line))
                continue;
            var row = pipes[i];
            _sb.Append("  ").Append(PipeId(i));
            if (_session.Decoder.GetPipeLabel(i) is { } label)
                _sb.Append(' ').Append(label);
            if (row.OpenerInvocation >= 0 && row.OpenerInvocation < invocations.Length)
                _sb.Append(" by ").Append(InvocationId(row.OpenerInvocation));
            _sb.Append(' ').Append(row.Lifecycle switch
            {
                PipeLifecycle.Open => "open",
                PipeLifecycle.Refused => "refused",
                PipeLifecycle.Closing => "closing",
                _ => "closed",
            });
            TextValues.AppendSize(_sb.Append(" in "), row.InboundBytes);
            TextValues.AppendSize(_sb.Append(" out "), row.OutboundBytes);
            if (row.Content != PipeContent.Raw)
            {
                var items = assembler.GetPipeItems(i, TapDirection.Inbound).Length + assembler.GetPipeItems(i, TapDirection.Outbound).Length;
                _sb.Append(Invariant, $" items {items}");
                if (row.Collection >= 0 && row.Collection < assembler.Collections.Length)
                {
                    var collection = assembler.Collections[row.Collection];
                    if (collection.LastVersion >= 0)
                        _sb.Append(Invariant, $" v{collection.LastVersion}");
                }
            }

            Stat();
        }

        // Gaps and noise.
        if (Next(ref line))
        {
            long inOps = 0, inBytes = 0, outOps = 0, outBytes = 0;
            var count = 0;
            foreach (var gap in _session.Gaps)
            {
                if (!Kept(gap.Direction, gap.FromTicks, filter))
                    continue;
                count++;
                if (gap.Direction == TapDirection.Inbound)
                {
                    inOps += gap.DroppedOps;
                    inBytes += gap.DroppedBytes;
                }
                else
                {
                    outOps += gap.DroppedOps;
                    outBytes += gap.DroppedBytes;
                }
            }

            _sb.Append(Invariant, $"gaps {count}");
            if (count > 0)
            {
                _sb.Append(Invariant, $" in {inOps} ops ");
                TextValues.AppendSize(_sb, inBytes).Append(Invariant, $" out {outOps} ops ");
                TextValues.AppendSize(_sb, outBytes).Append(" (Drop)");
            }

            Stat();
        }

        if (Next(ref line))
        {
            int pings = 0, credits = 0, windows = 0;
            foreach (var key in _session.FrameOrder)
            {
                if (!_session.TryGetFrame(key, out var frame) || frame.Kind != FrameKind.Frame || !Kept(frame.Direction, frame.StartTicks, filter))
                    continue;
                switch (frame.Type)
                {
                    case MessageType.Ping:
                        pings++;
                        break;
                    case MessageType.InvocationCredit:
                        credits++;
                        break;
                    case MessageType.PipeWindowUpdate:
                        windows++;
                        break;
                }
            }

            _sb.Append(Invariant, $"noise ping ×{pings} credit ×{credits} window ×{windows}");
            Stat();
        }

        if (_session.State != CaptureState.Open && Next(ref line))
            End();
    }

    // Moves to summary line `line`; false when it was written before the cursor.
    private bool Next(ref int line)
    {
        var index = line++;
        if (index < _frame)
            return false;
        BeginEvent(1, index, 0);
        _frame = index + 1;
        return true;
    }

    private void Stat() => Emit(NewRecord(TextRecordKind.Stat, _session.StartTicks, null) with { Value = Text() });

    private static bool Kept(TapDirection direction, long ticks, TextFilter filter)
    {
        if ((filter.Directions & (direction == TapDirection.Inbound ? TextDirections.Inbound : TextDirections.Outbound)) == 0)
            return false;
        return filter.Window is not { } window || window.Contains(ticks);
    }

    private long RequestStart(in InvocationRow row)
    {
        if (row.HasRequest && _session.Assembler.TryGetFrame(row.Request.Direction, row.Request.Seq, out var request))
            return request.StartTicks;
        if (!row.Result.IsNone && _session.Assembler.TryGetFrame(row.Result.Direction, row.Result.Seq, out var result))
            return result.StartTicks;
        return 0;
    }

    private sealed class MethodStats(string direction, string name)
    {
        private readonly List<double> _durations = [];

        public string Direction { get; } = direction;

        public string Name { get; } = name;

        public int Count { get; private set; }

        private int Ok { get; set; }

        private int Errors { get; set; }

        private int Canceled { get; set; }

        private int Pending { get; set; }

        private int OneWay { get; set; }

        public void Add(InvocationState state, double? duration)
        {
            Count++;
            switch (state)
            {
                case InvocationState.Completed:
                    Ok++;
                    break;
                case InvocationState.Exception:
                case InvocationState.Unauthorized:
                case InvocationState.Refused:
                    Errors++;
                    break;
                case InvocationState.Canceled:
                    Canceled++;
                    break;
                case InvocationState.OneWay:
                    OneWay++;
                    break;
                default:
                    Pending++;
                    break;
            }

            if (duration is { } ms && state != InvocationState.Pending)
                _durations.Add(ms);
        }

        public void AppendStates(System.Text.StringBuilder sb)
        {
            Part(sb, "ok", Ok);
            Part(sb, "err", Errors);
            Part(sb, "cancel", Canceled);
            Part(sb, "pending", Pending);
            Part(sb, "oneway", OneWay);
        }

        public void AppendLatency(System.Text.StringBuilder sb)
        {
            if (_durations.Count == 0)
                return;
            _durations.Sort();
            TextValues.AppendDuration(sb.Append(" p50 "), Rank(0.50));
            if (_durations.Count >= 10)
                TextValues.AppendDuration(sb.Append(" p99 "), Rank(0.99));
            TextValues.AppendDuration(sb.Append(" max "), _durations[^1]);
        }

        private double Rank(double p) => _durations[Math.Clamp((int)Math.Ceiling(p * _durations.Count) - 1, 0, _durations.Count - 1)];

        private static void Part(System.Text.StringBuilder sb, string word, int count)
        {
            if (count > 0)
                sb.Append(' ').Append(word).Append(' ').Append(count.ToString(Invariant));
        }
    }
}
