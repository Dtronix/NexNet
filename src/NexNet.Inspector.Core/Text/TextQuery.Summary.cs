using System.Text;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Text;

public sealed partial class TextQuery
{
    private const int SummaryMethods = 10;
    private const int SummaryIds = 3;

    /// <summary>
    /// `summary`: at most about 30 lines: totals, handshake, calls per method with p50/p99/max (the ten busiest),
    /// errors and cancellations, pipes, the largest messages, window stalls, gaps, noise, what was still open at the end,
    /// and the disconnect.
    /// </summary>
    /// <param name="destination">Receives the lines.</param>
    public void Summary(List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var stats = new List<TextRecord>();
        Render(stats, Options with { Level = TextLevel.Summary });

        var section = 0; // 0 before the calls, 1 calls, 2 pipes, 3 after
        var methods = 0;
        var hidden = 0;
        foreach (var record in stats)
        {
            if (record.Kind != TextRecordKind.Stat)
            {
                if (record.Kind == TextRecordKind.End)
                    destination.Add(Stat(OpenAtEnd()));
                destination.Add(record);
                continue;
            }

            var text = record.Value ?? "";
            if (text.StartsWith("calls ", StringComparison.Ordinal))
            {
                section = 1;
            }
            else if (text.StartsWith("pipes ", StringComparison.Ordinal))
            {
                if (hidden > 0)
                    destination.Add(Stat(string.Create(Invariant, $"  …+{hidden} methods")));
                destination.Add(Stat(ErrorsLine()));
                destination.Add(Stat(CancelsLine()));
                destination.Add(Stat(PipesLine()));
                destination.Add(Stat(LargestLine()));
                destination.Add(Stat(StallsLine()));
                section = 2;
                continue;
            }
            else if (section == 2 && text.StartsWith("  ", StringComparison.Ordinal))
            {
                continue;
            }
            else if (section == 2)
            {
                section = 3;
            }
            else if (section == 1 && text.StartsWith("  ", StringComparison.Ordinal) && ++methods > SummaryMethods)
            {
                hidden++;
                continue;
            }

            destination.Add(record);
        }

        // A live session has no end line yet.
        if (stats.Count > 0 && stats[^1].Kind != TextRecordKind.End)
            destination.Add(Stat(OpenAtEnd()));
    }

    private string ErrorsLine()
    {
        var errors = new List<TextRecord>();
        Render(errors, Options with
        {
            Level = TextLevel.Messages,
            Header = false,
            Filter = TextFilter.All with { Match = static r => IsError(r), MatchKey = "errors" },
        });
        _sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"errors {errors.Count}");
        for (var i = 0; i < errors.Count && i < SummaryIds; i++)
        {
            var e = errors[i];
            _sb.Append(i == 0 ? ": " : ", ").Append(e.Id ?? "END");
            if (e.Name is not null && e.Kind is TextRecordKind.Call or TextRecordKind.Result)
                _sb.Append(' ').Append(e.Name);
            _sb.Append(' ').Append(e.Kind switch
            {
                TextRecordKind.Problem or TextRecordKind.Pipe => e.Value,
                _ => e.Outcome,
            });
        }

        if (errors.Count > SummaryIds)
            _sb.Append(System.Globalization.CultureInfo.InvariantCulture, $", …+{errors.Count - SummaryIds}");
        return Text();
    }

    private string CancelsLine()
    {
        lock (_session.Gate)
        {
            var invocations = _session.Assembler.Invocations;
            var count = 0;
            for (var i = 0; i < invocations.Length; i++)
            {
                var row = invocations[i];
                if (row.State != InvocationState.Canceled)
                    continue;
                if (count < SummaryIds)
                {
                    _sb.Append(count == 0 ? ": " : ", ").Append(_ids.InvocationId(i)).Append(' ').Append(_ids.MethodName(row));
                    var start = row.HasRequest ? FrameTicks(row.Request.Direction, row.Request.Seq) : 0;
                    var end = row.Result.IsNone ? 0 : FrameEndTicks(row.Result.Direction, row.Result.Seq);
                    if (start > 0 && end > 0)
                        _sb.Append(' ').Append(Duration(_ids.Span(start, end)));
                }

                count++;
            }

            if (count > SummaryIds)
                _sb.Append(Invariant, $", …+{count - SummaryIds}");
            _sb.Insert(0, string.Create(Invariant, $"cancels {count}"));
            return Text();
        }
    }

    private string PipesLine()
    {
        lock (_session.Gate)
        {
            var pipes = _session.Assembler.Pipes;
            int open = 0, refused = 0, channels = 0, collections = 0;
            foreach (ref readonly var pipe in pipes)
            {
                if (pipe.Lifecycle is PipeLifecycle.Open or PipeLifecycle.Closing)
                    open++;
                if (pipe.Lifecycle == PipeLifecycle.Refused)
                    refused++;
                if (pipe.Content == PipeContent.Channel)
                    channels++;
                if (pipe.Content == PipeContent.Collection)
                    collections++;
            }

            _sb.Append(Invariant, $"pipes {pipes.Length}");
            if (pipes.Length > 0)
                _sb.Append(Invariant, $" (raw {pipes.Length - channels - collections} channel {channels} collection {collections}) open {open}");
            if (refused > 0)
                _sb.Append(Invariant, $" refused {refused}");
            return Text();
        }
    }

    private string LargestLine()
    {
        var candidates = new List<(long Bytes, string Label)>();
        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            var invocations = assembler.Invocations;
            for (var i = 0; i < invocations.Length; i++)
            {
                var row = invocations[i];
                var request = RequestBytes(row);
                if (request > 0)
                    candidates.Add((request, _ids.InvocationId(i) + " " + _ids.MethodName(row) + " arg"));
                var result = ResultBytes(row);
                if (result > 0)
                    candidates.Add((result, _ids.InvocationId(i) + " " + _ids.MethodName(row) + " result"));
            }

            var items = assembler.ChannelItems;
            var pipes = assembler.Pipes;
            for (var p = 0; p < pipes.Length; p++)
            {
                long largest = 0;
                foreach (var direction in (ReadOnlySpan<TapDirection>)[TapDirection.Inbound, TapDirection.Outbound])
                {
                    foreach (var item in assembler.GetPipeItems(p, direction))
                        largest = Math.Max(largest, items[item].Length);
                }

                if (largest > 0)
                    candidates.Add((largest, _ids.PipeId(p) + " item"));
            }
        }

        candidates.Sort(static (a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes) : string.CompareOrdinal(a.Label, b.Label));
        _sb.Append("largest");
        if (candidates.Count == 0)
            _sb.Append(" none");
        for (var i = 0; i < candidates.Count && i < SummaryIds; i++)
            TextValues.AppendSize(_sb.Append(i == 0 ? " " : ", ").Append(candidates[i].Label).Append(' '), candidates[i].Bytes);
        return Text();
    }

    // A pipe writer stalls when the bytes it sent reach the credit its reader granted (the Ready window plus window
    // updates); the stall lasts until the next grant.
    private string StallsLine()
    {
        var count = 0;
        double longest = 0;
        string? where = null;
        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            var pipes = assembler.Pipes;
            var events = assembler.PipeEvents;
            var timeline = new List<(long Ticks, long Grant, long Sent)>();
            for (var p = 0; p < pipes.Length; p++)
            {
                foreach (var writer in (ReadOnlySpan<TapDirection>)[TapDirection.Inbound, TapDirection.Outbound])
                {
                    timeline.Clear();
                    var reader = writer == TapDirection.Inbound ? TapDirection.Outbound : TapDirection.Inbound;
                    foreach (ref readonly var e in events)
                    {
                        if (e.Pipe == p && e.Direction == reader && e.Kind is PipeEventKind.Ready or PipeEventKind.WindowUpdate && e.Window > 0)
                            timeline.Add((FrameTicks(e.Direction, e.Seq), e.Window, 0));
                    }

                    if (timeline.Count == 0)
                        continue;
                    foreach (var run in assembler.GetPipeRuns(p, writer))
                    {
                        var message = assembler.Messages[run];
                        timeline.Add((FrameEndTicks(writer, message.LastSeq), 0, message.Length));
                    }

                    timeline.Sort(static (a, b) => a.Ticks != b.Ticks ? a.Ticks.CompareTo(b.Ticks) : b.Grant.CompareTo(a.Grant));
                    long granted = 0, sent = 0, stalledAt = 0;
                    foreach (var (ticks, grant, bytes) in timeline)
                    {
                        if (grant > 0)
                        {
                            granted += grant;
                            if (stalledAt > 0)
                            {
                                count++;
                                var ms = _ids.Span(stalledAt, ticks);
                                if (ms > longest)
                                {
                                    longest = ms;
                                    where = _ids.PipeId(p);
                                }

                                stalledAt = 0;
                            }

                            continue;
                        }

                        sent += bytes;
                        if (sent >= granted && stalledAt == 0)
                            stalledAt = ticks;
                    }
                }
            }
        }

        if (count == 0)
            return "stalls window none";
        return string.Create(Invariant, $"stalls window {count} (longest {Duration(longest)} on {where})");
    }

    private string OpenAtEnd()
    {
        var shown = 0;
        var total = 0;
        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            var invocations = assembler.Invocations;
            for (var i = 0; i < invocations.Length; i++)
            {
                if (invocations[i].State != InvocationState.Pending)
                    continue;
                total++;
                if (shown++ < SummaryIds + 2)
                    _sb.Append(shown == 1 ? " " : ", ").Append(_ids.InvocationId(i)).Append(' ').Append(_ids.MethodName(invocations[i])).Append(" pending");
            }

            var pipes = assembler.Pipes;
            for (var p = 0; p < pipes.Length; p++)
            {
                if (pipes[p].Lifecycle is not (PipeLifecycle.Open or PipeLifecycle.Closing))
                    continue;
                total++;
                if (shown++ < SummaryIds + 2)
                {
                    _sb.Append(shown == 1 ? " " : ", ").Append(_ids.PipeId(p));
                    if (_session.Decoder.GetPipeLabel(p) is { } label)
                        _sb.Append(' ').Append(label);
                    _sb.Append(' ').Append(LifecycleWord(pipes[p].Lifecycle));
                }
            }
        }

        if (total > SummaryIds + 2)
            _sb.Append(Invariant, $", …+{total - SummaryIds - 2}");
        if (total == 0)
            _sb.Append(" none");
        _sb.Insert(0, _session.State == CaptureState.Open ? "open now" : "open at end");
        return Text();
    }

    /// <summary>`pipes`: one line per pipe: what it carries, the call that opened it, its state, bytes and runs per direction,
    /// the window each side granted, items, and how long it was open.</summary>
    /// <param name="destination">Receives the lines.</param>
    public void Pipes(List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        AddHeader(destination);
        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            var pipes = assembler.Pipes;
            destination.Add(Stat(string.Create(Invariant, $"pipes {pipes.Length}")));
            for (var p = 0; p < pipes.Length; p++)
            {
                var row = pipes[p];
                long first = 0, last = 0;
                foreach (ref readonly var e in assembler.PipeEvents)
                {
                    if (e.Pipe != p)
                        continue;
                    var ticks = FrameTicks(e.Direction, e.Seq);
                    if (first == 0)
                        first = ticks;
                    last = Math.Max(last, ticks);
                }

                foreach (var direction in (ReadOnlySpan<TapDirection>)[TapDirection.Inbound, TapDirection.Outbound])
                {
                    foreach (var run in assembler.GetPipeRuns(p, direction))
                    {
                        var m = assembler.Messages[run];
                        var start = FrameTicks(direction, m.FirstSeq);
                        if (first == 0 || (start > 0 && start < first))
                            first = start;
                        last = Math.Max(last, FrameEndTicks(direction, m.LastSeq));
                    }
                }

                var label = _session.Decoder.GetPipeLabel(p);
                if (label is not null)
                    _sb.Append(label).Append(' ');
                if (row.OpenerInvocation >= 0 && row.OpenerInvocation < assembler.Invocations.Length)
                {
                    _sb.Append("by ").Append(_ids.InvocationId(row.OpenerInvocation)).Append(' ');
                    var method = _ids.MethodName(assembler.Invocations[row.OpenerInvocation]);
                    if (method != label)
                        _sb.Append(method).Append(' ');
                }
                _sb.Append(LifecycleWord(row.Lifecycle));
                TextValues.AppendSize(_sb.Append(" out "), row.OutboundBytes).Append(Invariant, $" ×{row.OutboundRuns}");
                TextValues.AppendSize(_sb.Append(" in "), row.InboundBytes).Append(Invariant, $" ×{row.InboundRuns}");
                TextValues.AppendSize(_sb.Append(" window out "), row.OutboundGrants);
                TextValues.AppendSize(_sb.Append(" in "), row.InboundGrants);
                if (row.Content != PipeContent.Raw)
                {
                    var items = assembler.GetPipeItems(p, TapDirection.Inbound).Length + assembler.GetPipeItems(p, TapDirection.Outbound).Length;
                    _sb.Append(Invariant, $" items {items}");
                }

                if (row.FromSnapshot)
                    _sb.Append(" from snapshot");
                if (row.ChannelUnavailable && row.Content != PipeContent.Raw)
                    _sb.Append(" items unavailable");
                if (first > 0 && last > first)
                    _sb.Append(' ').Append(Duration(_ids.Span(first, last)));

                destination.Add(Line(TextRecordKind.Pipe, first > 0 ? first : _session.StartTicks, row.OpenerDirection) with
                {
                    Id = _ids.PipeId(p),
                    Value = Text(),
                    Bytes = row.InboundBytes + row.OutboundBytes,
                    Entity = p,
                });
            }
        }
    }

    /// <summary>
    /// `collections [--contents]`: one line per synchronized collection (its version, messages and item count, rebuilt from
    /// the messages the server sent); with <paramref name="contents"/>, its items at the end of the capture.
    /// </summary>
    /// <param name="contents">List the items.</param>
    /// <param name="destination">Receives the lines.</param>
    public void Collections(bool contents, List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        AddHeader(destination);
        var items = new List<string>();
        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            var collections = assembler.Collections;
            destination.Add(Stat(string.Create(Invariant, $"collections {collections.Length}")));
            for (var c = 0; c < collections.Length; c++)
            {
                var row = collections[c];
                var complete = Rebuild(c, items, out var firstTicks, out var direction);
                var name = _session.Decoder.TryGetCollection(row.CollectionId, out var collection)
                    ? collection.Name
                    : string.Create(Invariant, $"m{row.CollectionId}");
                if (row.LastVersion >= 0)
                    _sb.Append(Invariant, $"v{row.LastVersion} ");
                _sb.Append(Invariant, $"messages {row.MessageCount} items {items.Count}");
                if (!complete)
                    _sb.Append(" (no reset seen: items since attach)");
                destination.Add(Line(TextRecordKind.Collection, firstTicks > 0 ? firstTicks : _session.StartTicks, direction) with
                {
                    Id = _ids.PipeId(row.Pipe),
                    Name = name,
                    Value = Text(),
                    Entity = row.Pipe,
                });
                if (!contents)
                    continue;
                for (var i = 0; i < items.Count; i++)
                    destination.Add(Field(1, string.Create(Invariant, $"[{i}]"), items[i]));
            }
        }
    }

    // Replays a collection's messages from the server into its items. Call under the gate. False when no reset was seen.
    private bool Rebuild(int collection, List<string> items, out long firstTicks, out TapDirection? direction)
    {
        items.Clear();
        firstTicks = 0;
        direction = null;
        var reset = false;
        var pending = new List<string>();
        var assembler = _session.Assembler;
        var values = new List<string>();
        foreach (ref readonly var message in assembler.CollectionMessages)
        {
            if (message.Collection != collection || message.Malformed || !IsServerDirection(message.Direction))
                continue;
            if (firstTicks == 0)
            {
                var item = assembler.ChannelItems[message.Item];
                firstTicks = FrameTicks(item.Direction, item.FirstSeq);
                direction = message.Direction;
            }

            values.Clear();
            if (!message.Value.IsNone)
                CollectionValues(_session.Decoder.DecodeCollectionMessage(message), values);
            switch (message.Kind)
            {
                case CollectionMessageKind.ResetStart:
                    pending.Clear();
                    break;
                case CollectionMessageKind.ResetValues:
                    pending.AddRange(values);
                    break;
                case CollectionMessageKind.ResetComplete:
                    items.Clear();
                    items.AddRange(pending);
                    pending.Clear();
                    reset = true;
                    break;
                case CollectionMessageKind.Clear:
                    items.Clear();
                    break;
                case CollectionMessageKind.Insert when values.Count > 0:
                    items.Insert(Math.Clamp(message.Position, 0, items.Count), values[0]);
                    break;
                case CollectionMessageKind.Replace when values.Count > 0 && (uint)message.Position < (uint)items.Count:
                    items[message.Position] = values[0];
                    break;
                case CollectionMessageKind.Move when (uint)message.Position < (uint)items.Count:
                    var moved = items[message.Position];
                    items.RemoveAt(message.Position);
                    items.Insert(Math.Clamp(message.ToPosition, 0, items.Count), moved);
                    break;
                case CollectionMessageKind.Remove when (uint)message.Position < (uint)items.Count:
                    items.RemoveAt(message.Position);
                    break;
            }
        }

        return reset;
    }

    // The value(s) a decoded collection message carries, one text per item.
    private void CollectionValues(DecodedValue decoded, List<string> values)
    {
        var fields = decoded.Fields;
        for (var i = 0; i < fields.Length; i++)
        {
            ref readonly var field = ref fields[i];
            if (field.Name is not ("Value" or "Values") || field.Kind is FieldKind.CollectionBody or FieldKind.CollectionMessage)
                continue;
            if (field.Name == "Value")
            {
                values.Add(_values.AppendField(_sb, decoded, i, 1).ToString());
                _sb.Clear();
                return;
            }

            switch (field.Kind)
            {
                case FieldKind.Sequence:
                    for (var child = field.FirstChild; child >= 0; child = fields[child].NextSibling)
                    {
                        values.Add(_values.AppendField(_sb, decoded, child, 1).ToString());
                        _sb.Clear();
                    }

                    return;
                case FieldKind.PrimitiveArray:
                    var kind = DecodedValue.GetElementKind(field);
                    for (var e = 0; decoded.TryGetElement(field, e, out var scalar); e++)
                        values.Add(Scalar(kind, scalar));
                    return;
                case FieldKind.Binary:
                    foreach (var b in decoded.GetContent(field))
                        values.Add(b.ToString(Invariant));
                    return;
                default:
                    values.Add(_values.AppendField(_sb, decoded, i, 1).ToString());
                    _sb.Clear();
                    return;
            }
        }
    }

    private static string Scalar(FieldKind kind, DecodedScalar value) => kind switch
    {
        FieldKind.Integer => value.Int64.ToString(Invariant),
        FieldKind.UnsignedInteger => value.UInt64.ToString(Invariant),
        FieldKind.Single => value.Single.ToString("R", Invariant),
        FieldKind.Double => value.Double.ToString("R", Invariant),
        FieldKind.Half => ((float)value.Half).ToString("R", Invariant),
        FieldKind.Char => "'" + value.Char + "'",
        _ => "?",
    };
}
