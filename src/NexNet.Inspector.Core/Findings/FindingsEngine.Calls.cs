using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Inspector.Core.Text;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Findings;

public sealed partial class FindingsEngine
{
    // Judges each invocation once it settled (its result arrived, or it is one-way); pending ones wait for the end.
    private void ScanInvocations()
    {
        var rows = _session.Assembler.Invocations;
        while (_judged.Count < rows.Length)
            _judged.Add(false);
        for (var i = _invocation; i < rows.Length; i++)
        {
            if (_judged[i] || rows[i].State == InvocationState.Pending)
                continue;
            _judged[i] = true;
            Judge(i, rows[i]);
        }

        while (_invocation < rows.Length && _judged[_invocation])
            _invocation++;
    }

    private void Judge(int index, in InvocationRow row)
    {
        if (!row.HasRequest)
        {
            Orphan(index, row);
            return;
        }

        if (row.State is not (InvocationState.Completed or InvocationState.Exception))
            return;
        var start = RequestStart(row);
        var end = ResultEnd(row);
        if (row.CancellationSeq >= 0)
            CancelRace(index, row, end);
        if (start > 0 && end > 0 && (row.Flags & InvocationFlags.DuplexPipe) == 0)
            Slow(index, row, start, end);
    }

    private void Orphan(int index, in InvocationRow row)
    {
        if (row.Result.IsNone)
            return;
        var assembler = _session.Assembler;
        var at = assembler.TryGetFrame(row.Result.Direction, row.Result.Seq, out var frame) ? frame.StartTicks : _session.StartTicks;
        var requestDirection = Opposite(row.Result.Direction);
        var attached = !_session.IsFromInception(requestDirection);
        var afterGap = GapBefore(requestDirection, at);
        var name = _ids.MethodName(row);
        _sb.Append(name == "?" ? "a result (" : name + " result (").Append(StateWord(row.State)).Append(')');
        _sb.Append(attached ? " of a call made before the capture attached"
            : afterGap ? " of a call whose request a gap dropped"
            : ": no request with this ID was captured");
        Add(attached || afterGap ? FindingSeverity.Info : FindingSeverity.Error, FindingKind.OrphanResult, Text(), [_ids.InvocationId(index)],
            at, Math.Max(at, ResultEnd(row)));
    }

    private void CancelRace(int index, in InvocationRow row, long end)
    {
        if (!_session.Assembler.TryGetFrame(row.Direction, row.CancellationSeq, out var cancel))
            return;
        var resultStart = ResultStart(row);
        var name = _ids.MethodName(row);
        if (resultStart <= 0 || cancel.StartTicks <= resultStart)
        {
            _sb.Append(name).Append(" was canceled but its result (").Append(StateWord(row.State)).Append(") still came ");
            TextValues.AppendDuration(_sb, Span(cancel.StartTicks, Math.Max(cancel.StartTicks, resultStart > 0 ? resultStart : end)))
                .Append(" later: the caller discarded it");
            Add(FindingSeverity.Warning, FindingKind.CancelRace, Text(), [_ids.InvocationId(index)], cancel.StartTicks, end);
        }
        else
        {
            _sb.Append(name).Append(" was canceled ");
            TextValues.AppendDuration(_sb, Span(resultStart, cancel.StartTicks)).Append(" after its result (").Append(StateWord(row.State))
                .Append(") was sent: they crossed");
            Add(FindingSeverity.Info, FindingKind.CancelRace, Text(), [_ids.InvocationId(index)], resultStart, cancel.StartTicks);
        }
    }

    private void Slow(int index, in InvocationRow row, long start, long end)
    {
        var duration = Span(start, end);
        var name = _ids.MethodName(row);
        var key = Arrow(row.Direction) + name;
        if (!_durations.TryGetValue(key, out var durations))
        {
            durations = [];
            _durations[key] = durations;
        }

        if (duration >= Options.SlowCallMs)
        {
            _sb.Append(name).Append(" took ");
            TextValues.AppendDuration(_sb, duration).Append(" (slow threshold ");
            TextValues.AppendDuration(_sb, Options.SlowCallMs).Append(')');
            Add(FindingSeverity.Warning, FindingKind.SlowCall, Text(), [_ids.InvocationId(index)], start, end);
        }
        else if (durations.Count >= Options.OutlierMinSamples)
        {
            Outlier(index, name, duration, durations, -1, start, end);
        }
        else
        {
            _deferred.Add(new Deferred(index, key, duration, start, end));
        }

        Insert(durations, duration);
    }

    // An outlier: longer than every other call of the method but rarely (their p99), many times their median.
    private void Outlier(int index, string name, double duration, List<double> durations, int skip, long start, long end)
    {
        var count = skip >= 0 ? durations.Count - 1 : durations.Count;
        if (count < Options.OutlierMinSamples)
            return;
        var median = Percentile(durations, 0.5, skip);
        var p99 = Percentile(durations, 0.99, skip);
        if (duration <= p99 || duration < Options.OutlierFloorMs || duration < Options.OutlierFactor * median)
            return;
        _sb.Append(name).Append(" took ");
        TextValues.AppendDuration(_sb, duration).Append(Invariant, $"; its other {count} calls: median ");
        Short(median).Append(", p99 ");
        Short(p99);
        Add(FindingSeverity.Warning, FindingKind.SlowCall, Text(), [_ids.InvocationId(index)], start, end);
    }

    // A duration, `<0.1ms` below what one decimal shows.
    private System.Text.StringBuilder Short(double ms) => ms < 0.05 ? _sb.Append("<0.1ms") : TextValues.AppendDuration(_sb, ms);

    // The value at rank p of a sorted list, leaving one index out.
    private static double Percentile(List<double> sorted, double p, int skip)
    {
        var count = skip >= 0 ? sorted.Count - 1 : sorted.Count;
        var rank = Math.Clamp((int)Math.Ceiling(p * count) - 1, 0, count - 1);
        return sorted[skip >= 0 && rank >= skip ? rank + 1 : rank];
    }

    // Calls judged before their method had enough samples: judged again against all its calls.
    private void JudgeDeferred()
    {
        var rows = _session.Assembler.Invocations;
        foreach (var deferred in _deferred)
        {
            var durations = _durations[deferred.Key];
            var skip = durations.BinarySearch(deferred.Duration);
            if (skip < 0 || (uint)deferred.Invocation >= (uint)rows.Length)
                continue;
            Outlier(deferred.Invocation, _ids.MethodName(rows[deferred.Invocation]), deferred.Duration, durations, skip, deferred.Start, deferred.End);
        }

        _deferred.Clear();
    }

    private void PendingAtEnd()
    {
        var rows = _session.Assembler.Invocations;
        var disconnected = _session.Close is { Kind: NntlCloseKind.Disconnected };
        var end = _session.Close?.Ticks ?? _session.EndTicks;
        for (var i = _invocation; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.State != InvocationState.Pending || (i < _judged.Count && _judged[i]))
                continue;
            var start = RequestStart(row);
            _sb.Append(_ids.MethodName(row)).Append(row.CancellationSeq >= 0 ? " (canceled)" : "").Append(" had no result ");
            if (start > 0)
                TextValues.AppendDuration(_sb, Span(start, end)).Append(" after it was sent, ");
            _sb.Append(disconnected ? "at disconnect" : "when the capture ended");
            var lost = start > 0 && _session.Gaps.Length > 0 && _session.Gaps[^1].ToTicks >= start;
            if (lost)
                _sb.Append(" (a gap may have dropped it)");
            Add(disconnected && !lost ? FindingSeverity.Warning : FindingSeverity.Info, FindingKind.PendingAtEnd, Text(), [_ids.InvocationId(i)],
                start > 0 ? start : end, end);
        }
    }

    private void PipesAtEnd()
    {
        var assembler = _session.Assembler;
        var pipes = assembler.Pipes;
        var invocations = assembler.Invocations;
        var disconnected = _session.Close is { Kind: NntlCloseKind.Disconnected };
        var end = _session.Close?.Ticks ?? _session.EndTicks;
        for (var i = 0; i < pipes.Length; i++)
        {
            var pipe = pipes[i];
            // A collection's pipe lives as long as its subscription, usually the session.
            if (pipe.Lifecycle is PipeLifecycle.Closed or PipeLifecycle.Refused || pipe.Content == PipeContent.Collection)
                continue;
            var opened = pipe.OpenSeq >= 0 && pipe.OpenerDirection is { } direction
                && assembler.TryGetFrame(OpenDirection(pipe), pipe.OpenSeq, out var open) ? open.StartTicks : _session.StartTicks;
            if (_session.Decoder.GetPipeLabel(i) is { } label)
                _sb.Append(label).Append(' ');
            var state = pipe.InboundState | pipe.OutboundState;
            _sb.Append(pipe.Lifecycle == PipeLifecycle.Closing
                ? (state & PipeStateFlags.ClientWriterServerReaderComplete) != 0 ? "client done, the server never completed" : "server done, the client never completed"
                : "open, never completed");
            _sb.Append(disconnected ? " before disconnect" : " when the capture ended");

            // A gap after it opened may have dropped its completion.
            var lost = _session.Gaps.Length > 0 && _session.Gaps[^1].ToTicks >= opened;
            if (lost)
                _sb.Append(" (a gap may have dropped its completion)");
            string[] ids = pipe.OpenerInvocation >= 0 && pipe.OpenerInvocation < invocations.Length
                ? [_ids.PipeId(i), _ids.InvocationId(pipe.OpenerInvocation)]
                : [_ids.PipeId(i)];
            Add(disconnected && !lost ? FindingSeverity.Warning : FindingSeverity.Info, FindingKind.PipeOpenAtEnd, Text(), ids, opened, end);
        }
    }

    // The Ready (or Refused) frame travels from the side that registered the pipe, opposite the opener's invocation.
    private static TapDirection OpenDirection(in PipeRow pipe) => Opposite(pipe.OpenerDirection ?? TapDirection.Inbound);

    // How the session ended: a failed close, a handshake that never completed, or a capture cut off.
    private void Ending()
    {
        var end = _session.Close?.Ticks ?? _session.EndTicks;
        if (_session.State == CaptureState.Truncated)
        {
            Add(FindingSeverity.Info, FindingKind.Disconnect, "the capture ends without a close: the recording was cut off", [SessionTag], end, end);
            return;
        }

        if (_session.Close is not { Kind: NntlCloseKind.Disconnected } close)
            return;
        var reason = close.DisconnectReason;
        var side = DisconnectSide();
        var from = side is null ? "" : " from " + side;
        switch (reason)
        {
            case DisconnectReason.None:
            case DisconnectReason.Graceful:
            case DisconnectReason.ServerShutdown:
            case DisconnectReason.ServerRestarting:
                break;
            case DisconnectReason.ClientMismatch:
            case DisconnectReason.ServerMismatch:
                Add(FindingSeverity.Error, FindingKind.SchemaMismatch, $"disconnected ({reason}{from}): the peers' nexus versions do not match",
                    [SessionTag], end, end);
                return;
            case DisconnectReason.ProtocolError:
            case DisconnectReason.FlowControl:
            case DisconnectReason.Stalled:
                Add(FindingSeverity.Error, FindingKind.Disconnect, $"disconnected with {reason}{from}", [SessionTag], end, end);
                break;
            default:
                Add(FindingSeverity.Warning, FindingKind.Disconnect, $"disconnected with {reason}{from}", [SessionTag], end, end);
                break;
        }

        // Captured from the first byte, closed by a disconnect, and no ServerGreeting: the handshake failed.
        if ((_session.IsFromInception(TapDirection.Inbound) || _session.IsFromInception(TapDirection.Outbound))
            && reason is not (DisconnectReason.Authentication or DisconnectReason.Unauthorized or DisconnectReason.RateLimited)
            && !HasServerGreeting())
        {
            Add(FindingSeverity.Error, FindingKind.ProtocolError, $"the handshake never completed (no ServerGreeting before {reason})",
                [SessionTag], end, end);
        }
    }

    private bool HasServerGreeting()
    {
        foreach (var greeting in _session.Assembler.Greetings)
        {
            if (greeting.Kind == GreetingKind.Server)
                return true;
        }

        return false;
    }

    // The side whose disconnect frame came last.
    private string? DisconnectSide()
    {
        var order = _session.FrameOrder;
        for (var i = order.Length - 1; i >= 0 && i >= order.Length - 64; i--)
        {
            if (!_session.TryGetFrame(order[i], out var frame) || frame.Kind != Framing.FrameKind.Frame || (byte)frame.Type is < 20 or > 39)
                continue;
            var tappedIsServer = _session.Role == NntlSessionRole.Server;
            return (frame.Direction == TapDirection.Outbound ? tappedIsServer : !tappedIsServer) ? "server" : "client";
        }

        return null;
    }

    // Large messages: judged once assembled (or aborted, truncated, orphaned), in order.
    private void ScanMessages()
    {
        var assembler = _session.Assembler;
        var messages = assembler.Messages;
        for (; _message < messages.Length; _message++)
        {
            var message = messages[_message];
            if (message.State == AssemblyState.Assembling)
                return;
            if (message.Kind != AssembledMessageKind.Large)
                continue;

            var start = message.FirstSeq >= 0 && assembler.TryGetFrame(message.Direction, message.FirstSeq, out var first) ? first.StartTicks : _session.StartTicks;
            var end = assembler.TryGetFrame(message.Direction, message.LastSeq, out var last) ? last.EndTicks : start;
            var size = message.TotalLength >= 0 ? message.TotalLength : message.Length;
            var what = message.LargeKind == LargeMessageKind.Argument ? " argument " : " result ";
            var invocations = assembler.Invocations;
            var known = message.Invocation >= 0 && message.Invocation < invocations.Length;
            string name = known ? _ids.MethodName(invocations[message.Invocation]) : "?";
            string[] ids = known ? [_ids.InvocationId(message.Invocation)] : [TextRenderer.FrameId(FrameIndexAtOrBefore(start))];
            var limit = _limit[(int)message.Direction];

            if (message.State == AssemblyState.Orphan)
            {
                var explained = !_session.IsFromInception(message.Direction) || GapBefore(message.Direction, start);
                _sb.Append(name).Append(what);
                TextValues.AppendSize(_sb.Append("chunks ("), message.Length).Append(") whose MessageStart was not captured");
                if (explained)
                    _sb.Append(" (attached mid-stream or after a gap)");
                Add(explained ? FindingSeverity.Info : FindingSeverity.Error, FindingKind.OrphanResult, Text(), ids, start, end);
            }
            else if (message.State == AssemblyState.Aborted && message.AbortReason is MessageAbortReason.TooLarge or MessageAbortReason.Busy)
            {
                _sb.Append(name).Append(what);
                TextValues.AppendSize(_sb, size).Append(Invariant, $" aborted by the receiver ({message.AbortReason})");
                Add(FindingSeverity.Warning, FindingKind.LargeMessage, Text(), ids, start, end);
            }
            else if (limit > 0 && size >= Options.NearLimitFraction * limit)
            {
                _sb.Append(name).Append(what);
                TextValues.AppendSize(_sb, size).Append(Invariant, $" is {100.0 * size / limit:0}% of the receiver's MaxMessageSize ");
                TextValues.AppendSize(_sb, limit);
                Add(FindingSeverity.Warning, FindingKind.LargeMessage, Text(), ids, start, end);
            }
            else if (size >= Options.LargeMessageBytes)
            {
                _sb.Append(name).Append(what);
                TextValues.AppendSize(_sb, size).Append(Invariant, $" in {Math.Max(0, message.FragmentCount - 1)} chunks over ");
                TextValues.AppendDuration(_sb, Span(start, end));
                Add(FindingSeverity.Info, FindingKind.LargeMessage, Text(), ids, start, end);
            }
        }
    }

    // Large channel items (and collection values), judged once complete, in order.
    private void ScanItems()
    {
        var assembler = _session.Assembler;
        var items = assembler.ChannelItems;
        for (; _item < items.Length; _item++)
        {
            var item = items[_item];
            if (item.State == AssemblyState.Assembling)
                return;
            var limit = _limit[(int)item.Direction];
            var near = limit > 0 && item.Length >= Options.NearLimitFraction * limit;
            if (!near && item.Length < Options.LargeMessageBytes)
                continue;
            var start = assembler.TryGetFrame(item.Direction, item.FirstSeq, out var first) ? first.StartTicks : _session.StartTicks;
            var end = assembler.TryGetFrame(item.Direction, item.LastSeq, out var last) ? last.EndTicks : start;
            _sb.Append(_session.Decoder.GetPipeLabel(item.Pipe) ?? "channel").Append(" item ");
            TextValues.AppendSize(_sb, item.Length);
            if (near)
                TextValues.AppendSize(_sb.Append(Invariant, $" is {100.0 * item.Length / limit:0}% of the receiver's MaxMessageSize "), limit);
            Add(near ? FindingSeverity.Warning : FindingSeverity.Info, FindingKind.LargeMessage, Text(), [_ids.PipeId(item.Pipe)], start, end);
        }
    }

    private int FrameIndexAtOrBefore(long ticks)
    {
        var order = _session.FrameOrder;
        var index = 0;
        for (var i = 0; i < order.Length && i < _frame; i++)
        {
            if (_session.TryGetFrame(order[i], out var frame) && frame.StartTicks <= ticks)
                index = i;
        }

        return index;
    }

    // True when a gap of the direction began at or before the ticks.
    private bool GapBefore(TapDirection direction, long ticks)
    {
        foreach (var gap in _session.Gaps)
        {
            if (gap.Direction == direction && gap.FromTicks <= ticks)
                return true;
        }

        return false;
    }

    private long RequestStart(in InvocationRow row)
        => row.HasRequest && _session.Assembler.TryGetFrame(row.Request.Direction, row.Request.Seq, out var request) ? request.StartTicks : 0;

    private long ResultStart(in InvocationRow row)
        => !row.Result.IsNone && _session.Assembler.TryGetFrame(row.Result.Direction, row.Result.Seq, out var result) ? result.StartTicks : 0;

    // The ticks of the result's last byte; 0 when no result.
    private long ResultEnd(in InvocationRow row)
    {
        var assembler = _session.Assembler;
        switch (row.Result.Kind)
        {
            case MessageRefKind.Frame when assembler.TryGetFrame(row.Result.Direction, row.Result.Seq, out var frame):
                return frame.EndTicks;
            case MessageRefKind.Assembled when (uint)row.Result.Index < (uint)assembler.Messages.Length:
                var message = assembler.Messages[row.Result.Index];
                return assembler.TryGetFrame(message.Direction, message.LastSeq, out var last) ? last.EndTicks : 0;
            default:
                return 0;
        }
    }

    private static string StateWord(InvocationState state) => state switch
    {
        InvocationState.Completed => "ok",
        InvocationState.Exception => "exception",
        InvocationState.Canceled => "canceled",
        InvocationState.Unauthorized => "unauthorized",
        InvocationState.Refused => "refused",
        InvocationState.OneWay => "oneway",
        _ => "…",
    };
}
