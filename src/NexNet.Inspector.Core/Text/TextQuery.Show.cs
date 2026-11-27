using System.Text;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Text;

public sealed partial class TextQuery
{
    private const int ShowElements = 16;
    private const int ShowHexBytes = 4096;
    private const int HexRow = 32;

    /// <summary>
    /// `show &lt;id&gt;`: the header, then for each entity the ID names its line and a compact YAML-like tree: an invocation's
    /// method, state, arguments and result (or exception) decoded field by field, its frames and pipe; a pipe's properties and
    /// its lines; a frame's properties. With <paramref name="bytes"/> every field also shows its byte range in the message
    /// (`@offset+size`) and a frame its bytes in hex.
    /// </summary>
    /// <param name="id">The ID (see <see cref="TryResolve"/>).</param>
    /// <param name="bytes">Add byte ranges and hex.</param>
    /// <param name="destination">Receives the lines.</param>
    /// <returns>Entities shown; -1 when <paramref name="id"/> is not an ID.</returns>
    public int Show(string id, bool bytes, List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var found = new List<TextEntityRef>();
        if (!TryResolve(id, found))
            return -1;
        AddHeader(destination);
        foreach (var entity in found)
        {
            switch (entity.Kind)
            {
                case TextEntityKind.Invocation:
                    ShowInvocation(entity.Index, bytes, destination);
                    break;
                case TextEntityKind.Pipe:
                    ShowPipe(entity.Index, destination);
                    break;
                case TextEntityKind.Frame:
                    ShowFrame(entity.Index, bytes, destination);
                    break;
            }
        }

        return found.Count;
    }

    private void ShowInvocation(int invocation, bool bytes, List<TextRecord> destination)
    {
        Render(destination, Options with
        {
            Level = TextLevel.Messages,
            Collapse = false,
            Header = false,
            Filter = TextFilter.All with
            {
                Match = r => r.Entity == invocation && r.Kind is TextRecordKind.Call or TextRecordKind.Result,
                MatchKey = "show",
            },
        });

        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            var row = assembler.Invocations[invocation];
            var decoder = _session.Decoder;
            _sb.Append(_ids.MethodName(row));
            if (row.RequestParsed)
                _sb.Append(Invariant, $" (method {row.MethodId})");
            if (decoder.IsClientCall(row) is { } client)
                _sb.Append(client ? " client → server" : " server → client");
            destination.Add(Field(0, "method", Text()));

            _sb.Append(StateWord(row.State));
            var start = row.HasRequest ? FrameTicks(row.Request.Direction, row.Request.Seq) : 0;
            var end = row.Result.IsNone ? 0 : FrameEndTicks(row.Result.Direction, row.Result.Seq);
            if (start > 0 && end > 0)
                _sb.Append(' ').Append(Duration(_ids.Span(start, end)));
            if (row.CancellationSeq >= 0)
                _sb.Append(" (cancel sent)");
            destination.Add(Field(0, "state", Text()));

            if (!row.HasRequest)
            {
                destination.Add(Field(0, "args", "(request not captured)"));
            }
            else
            {
                var size = RequestBytes(row);
                var args = decoder.DecodeArguments(row);
                Tree("args", args, size, bytes, destination);
            }

            if (!row.Result.IsNone && !row.Value.IsNone)
                Tree(row.State == InvocationState.Exception ? "exception" : "result", decoder.DecodeResult(row), ResultBytes(row), bytes, destination);

            // Frames of the invocation, in capture order.
            var order = _session.FrameOrder;
            var frames = new List<int>();
            for (var i = 0; i < order.Length; i++)
            {
                var link = assembler.GetLink(order[i].Direction, order[i].Seq);
                if (link.Entity == EntityKind.Invocation && link.EntityIndex == invocation)
                    frames.Add(i);
            }

            for (var i = 0; i < frames.Count; i++)
            {
                if (i == 12)
                {
                    _sb.Append(Invariant, $" …+{frames.Count - i}");
                    break;
                }

                _sb.Append(i == 0 ? "" : " ").Append(TextRenderer.FrameId(frames[i]));
            }

            destination.Add(Field(0, "frames", Text()));
            var pipes = assembler.Pipes;
            for (var p = 0; p < pipes.Length; p++)
            {
                if (pipes[p].OpenerInvocation == invocation)
                    destination.Add(Field(0, "pipe", _ids.PipeId(p) + " " + LifecycleWord(pipes[p].Lifecycle)));
            }
        }
    }

    // A decoded value as a tree under one name.
    private void Tree(string name, DecodedValue value, long size, bool bytes, List<TextRecord> destination)
    {
        var fields = value.Fields;
        if (fields.Length == 0)
        {
            destination.Add(Field(0, name, size > 0 ? "(bytes not held) " + Size(size) : "(none)"));
            return;
        }

        if (value.Problem != DecodeProblem.None && fields[0].Kind == FieldKind.Raw)
        {
            destination.Add(Field(0, name, "raw " + Size(fields[0].Length) + " (" + value.Problem + ")"));
            return;
        }

        if (fields[0].Kind == FieldKind.Arguments)
        {
            destination.Add(Field(0, name, fields[0].ChildCount == 0 ? "()" : null));
            Children(value, 0, 1, bytes, destination);
        }
        else
        {
            Node(value, 0, 0, name, bytes, destination);
        }

        if (value.Problem != DecodeProblem.None)
            destination.Add(Field(1, "problem", value.Problem.ToString()));
    }

    private void Children(DecodedValue value, int parent, int depth, bool bytes, List<TextRecord> destination)
    {
        var fields = value.Fields;
        var shown = 0;
        for (var child = fields[parent].FirstChild; child >= 0; child = fields[child].NextSibling)
        {
            if (shown++ == ShowElements)
            {
                destination.Add(Field(depth, null, string.Create(Invariant, $"…+{fields[parent].ChildCount - ShowElements}")));
                return;
            }

            Node(value, child, depth, null, bytes, destination);
        }
    }

    private void Node(DecodedValue value, int index, int depth, string? name, bool bytes, List<TextRecord> destination)
    {
        var fields = value.Fields;
        ref readonly var field = ref fields[index];
        if (field.Kind == FieldKind.Union && field.ChildCount >= 2)
        {
            var last = -1;
            for (var child = field.FirstChild; child >= 0; child = fields[child].NextSibling)
                last = child;
            Node(value, last, depth, name ?? field.Name, bytes, destination);
            return;
        }

        name ??= field.Name;
        if (name is null && field.Parent >= 0 && fields[field.Parent].Kind is FieldKind.Arguments or FieldKind.Tuple)
            name = string.Create(Invariant, $"[{field.ElementIndex}]");

        long offset = 0, length = -1;
        if (bytes)
        {
            if (value.Range.IsNone)
            {
                offset = field.Offset;
                length = field.Length;
            }
            else
            {
                var range = value.GetRange(field);
                offset = range.Offset;
                length = range.Length;
            }
        }

        string? summary = field.Kind switch
        {
            FieldKind.Object or FieldKind.ExceptionDetail => field.Decl?.Name ?? (field.Kind == FieldKind.ExceptionDetail ? "exception" : null),
            FieldKind.Sequence => string.Create(Invariant, $"[{field.ChildCount}]"),
            FieldKind.Map => string.Create(Invariant, $"{{{field.ChildCount}}}"),
            FieldKind.Tuple => string.Create(Invariant, $"({field.ChildCount})"),
            FieldKind.CollectionMessage or FieldKind.CollectionBody or FieldKind.Arguments => null,
            _ => "",
        };

        var container = field.Kind is FieldKind.Object or FieldKind.ExceptionDetail or FieldKind.Sequence or FieldKind.Map
            or FieldKind.Tuple or FieldKind.CollectionMessage or FieldKind.CollectionBody or FieldKind.Arguments;
        if (!container || field.ChildCount == 0 || depth >= Math.Max(1, Options.Depth))
        {
            var text = _values.AppendField(_sb, value, index, Math.Max(0, _values.Depth - 1)).ToString();
            _sb.Clear();
            destination.Add(Field(depth, name, text, offset, length));
            return;
        }

        destination.Add(Field(depth, name, summary is { Length: > 0 } ? summary : null, offset, length));
        Children(value, index, depth + 1, bytes, destination);
    }

    private void ShowPipe(int pipe, List<TextRecord> destination)
    {
        var all = new List<TextRecord>();
        Pipes(all);
        foreach (var record in all)
        {
            if (record.Kind == TextRecordKind.Pipe && record.Entity == pipe)
                destination.Add(record);
        }

        string id;
        lock (_session.Gate)
            id = _ids.PipeId(pipe);
        Render(destination, Options with
        {
            Level = TextLevel.Messages,
            Header = false,
            Filter = TextFilter.All with
            {
                Match = r => r.Id == id && r.Kind is TextRecordKind.Pipe or TextRecordKind.Item or TextRecordKind.Collection or TextRecordKind.Data,
                MatchKey = "show " + id,
            },
        });
    }

    private void ShowFrame(int orderIndex, bool bytes, List<TextRecord> destination)
    {
        Render(destination, Options with
        {
            Level = TextLevel.Frames,
            Collapse = false,
            Header = false,
            Filter = TextFilter.All with { Match = r => r.Frame == orderIndex && r.Kind == TextRecordKind.Frame, MatchKey = "show frame" },
        });

        lock (_session.Gate)
        {
            var key = _session.FrameOrder[orderIndex];
            if (!_session.TryGetFrame(key, out var frame))
                return;
            var assembler = _session.Assembler;
            destination.Add(Field(0, "type", frame.Kind switch
            {
                FrameKind.Preamble => "Preamble",
                FrameKind.Malformed => "Malformed",
                _ => TextRenderer.TypeName(frame.Type),
            }));
            destination.Add(Field(0, "direction", frame.Direction == TapDirection.Outbound ? "> sent" : "< received"));
            destination.Add(Field(0, "stream", string.Create(Invariant,
                $"@{frame.Offset}+{frame.Length} (header {frame.HeaderLength + frame.PostHeaderLength}, body {frame.BodyLength})")));
            destination.Add(Field(0, "ops", frame.FirstOpId == frame.LastOpId
                ? string.Create(Invariant, $"{frame.FirstOpId}")
                : string.Create(Invariant, $"{frame.FirstOpId}..{frame.LastOpId}")));
            var link = frame.Kind == FrameKind.Frame ? assembler.GetLink(frame.Direction, frame.Seq) : FrameLink.None;
            switch (link.Entity)
            {
                case EntityKind.Invocation when (uint)link.EntityIndex < (uint)assembler.Invocations.Length:
                    destination.Add(Field(0, "entity",
                        _ids.InvocationId(link.EntityIndex) + " " + _ids.MethodName(assembler.Invocations[link.EntityIndex])));
                    break;
                case EntityKind.Pipe when (uint)link.EntityIndex < (uint)assembler.Pipes.Length:
                    destination.Add(Field(0, "entity", _ids.PipeId(link.EntityIndex)));
                    break;
                case EntityKind.Greeting:
                    destination.Add(Field(0, "entity", "handshake"));
                    break;
            }

            var message = frame.Kind == FrameKind.Frame ? assembler.GetMessage(frame.Direction, frame.Seq) : MessageRef.None;
            if (message.Kind == MessageRefKind.Assembled && (uint)message.Index < (uint)assembler.Messages.Length)
            {
                var m = assembler.Messages[message.Index];
                _sb.Append(m.Kind == AssembledMessageKind.Large ? "large message, " : "pipe run, ");
                _sb.Append(Invariant, $"{m.FragmentCount} fragments, ");
                TextValues.AppendSize(_sb, m.TotalLength >= 0 ? m.TotalLength : m.Length);
                _sb.Append(", ").Append(m.State.ToString().ToLowerInvariant());
                destination.Add(Field(0, "message", Text()));
            }

            if ((link.Flags & FrameLinkFlags.BodyMalformed) != 0)
                destination.Add(Field(0, "problem", "body malformed"));

            if (!bytes || frame.Length <= 0)
                return;
            var shown = Math.Min(frame.Length, ShowHexBytes);
            var buffer = new byte[shown];
            if (!_session.GetStore(frame.Direction).TryCopyTo(frame.Offset, buffer))
            {
                destination.Add(Field(0, "bytes", "(no longer held)"));
                return;
            }

            destination.Add(Field(0, "bytes", null));
            for (var at = 0; at < shown; at += HexRow)
            {
                _sb.Append(Invariant, $"{at:x4} ");
                foreach (var b in buffer.AsSpan(at, Math.Min(HexRow, shown - at)))
                    _sb.Append(Invariant, $"{b:x2}");
                destination.Add(Line(TextRecordKind.Bytes, frame.StartTicks, frame.Direction) with
                {
                    Id = TextRenderer.FrameId(orderIndex),
                    Value = Text(),
                    Frame = orderIndex,
                });
            }

            if (shown < frame.Length)
                destination.Add(Field(1, null, "…+" + Size(frame.Length - shown)));
        }
    }

    /// <summary>
    /// `trace &lt;id&gt;`: for each message of the entity (an invocation's request and result, a pipe's data runs, a frame's
    /// message), the frames that carried it with the bytes each carried (`msg offset+size`) and the transport ops, the
    /// reads (or writes) those frames arrived in, and the other frames interleaved with it on the wire.
    /// </summary>
    /// <param name="id">The ID (see <see cref="TryResolve"/>).</param>
    /// <param name="destination">Receives the lines.</param>
    /// <returns>Entities traced; -1 when <paramref name="id"/> is not an ID.</returns>
    public int Trace(string id, List<TextRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var found = new List<TextEntityRef>();
        if (!TryResolve(id, found))
            return -1;
        AddHeader(destination);
        var messages = new List<(string What, MessageRef Message)>();
        foreach (var entity in found)
        {
            messages.Clear();
            lock (_session.Gate)
            {
                var assembler = _session.Assembler;
                switch (entity.Kind)
                {
                    case TextEntityKind.Invocation:
                        var row = assembler.Invocations[entity.Index];
                        var name = _ids.InvocationId(entity.Index) + " " + _ids.MethodName(row);
                        if (row.HasRequest)
                            messages.Add(("request " + name, row.Request));
                        if (!row.Result.IsNone)
                            messages.Add(("result " + name, row.Result));
                        break;
                    case TextEntityKind.Pipe:
                        var pipeId = _ids.PipeId(entity.Index);
                        foreach (var direction in (ReadOnlySpan<TapDirection>)[TapDirection.Outbound, TapDirection.Inbound])
                        {
                            var runs = assembler.GetPipeRuns(entity.Index, direction);
                            for (var r = 0; r < runs.Length; r++)
                                messages.Add((string.Create(Invariant, $"run {Arrow(direction)}{pipeId} {r + 1}/{runs.Length}"), assembler.Messages[runs[r]].Ref));
                        }

                        break;
                    case TextEntityKind.Frame:
                        var key = _session.FrameOrder[entity.Index];
                        var message = assembler.GetMessage(key.Direction, key.Seq);
                        messages.Add(("message of " + TextRenderer.FrameId(entity.Index),
                            message.IsNone ? MessageRef.ForFrame(key.Direction, key.Seq) : message));
                        break;
                }
            }

            foreach (var (what, message) in messages)
                TraceMessage(what, message, destination);
        }

        return found.Count;
    }

    private void TraceMessage(string what, MessageRef message, List<TextRecord> destination)
    {
        var fragments = new List<TraceFragment>();
        var interleaved = new List<FrameRecord>();
        var opLines = new List<TextRecord>();
        var interleavedFrames = new HashSet<int>();
        lock (_session.Gate)
        {
            var assembler = _session.Assembler;
            assembler.GetFragments(message, fragments);
            assembler.GetInterleaved(message, interleaved);
            long total = 0;
            foreach (var f in fragments)
                total += f.Length;
            _sb.Append(what).Append(Invariant, $": {fragments.Count} fragments ");
            TextValues.AppendSize(_sb, total);
            _sb.Append(Invariant, $", {interleaved.Count} interleaved");
            destination.Add(Stat(Text()));

            var seenOps = new HashSet<(TapDirection, long)>();
            foreach (var f in fragments)
            {
                assembler.TryGetFrame(f.Direction, f.Seq, out var frame);
                var orderIndex = OrderIndex(f.Direction, f.Seq);
                _sb.Append(f.FirstOpId == f.LastOpId ? string.Create(Invariant, $"op {f.FirstOpId}") : string.Create(Invariant, $"op {f.FirstOpId}..{f.LastOpId}"));
                if ((frame.Flags & FrameFlags.TransportSplit) != 0)
                    _sb.Append(" T");
                if ((frame.Flags & FrameFlags.NexNetSplit) != 0)
                    _sb.Append(" N");
                destination.Add(Line(TextRecordKind.Fragment, frame.StartTicks, f.Direction) with
                {
                    Id = orderIndex >= 0 ? TextRenderer.FrameId(orderIndex) : string.Create(Invariant, $"seq{f.Seq}"),
                    Name = TextRenderer.TypeName(frame.Type),
                    Value = Text(),
                    Offset = f.MessageOffset,
                    Bytes = f.Length,
                    Frame = orderIndex,
                });

                // The transport ops the frame arrived in.
                var store = _session.GetStore(f.Direction);
                if (!store.TryFindOp(frame.Offset, out var op))
                    continue;
                var ops = store.Ops;
                for (; op < ops.Length && ops[op].Offset < frame.Offset + Math.Max(1, frame.Length); op++)
                {
                    if (!seenOps.Add((f.Direction, ops[op].OpId)))
                        continue;
                    TextValues.AppendTime(_sb, _ids.Milliseconds(ops[op].Ticks)).Append(' ');
                    TextValues.AppendSize(_sb, ops[op].Length).Append(Invariant, $" @{ops[op].Offset}");
                    opLines.Add(Field(1, string.Create(Invariant, $"{Arrow(f.Direction)}op {ops[op].OpId}"), Text()));
                }
            }

            foreach (var frame in interleaved)
            {
                var index = OrderIndex(frame.Direction, frame.Seq);
                if (index >= 0)
                    interleavedFrames.Add(index);
            }
        }

        var reads = 0;
        var writes = 0;
        foreach (var line in opLines)
        {
            if (line.Name![0] == '<')
                reads++;
            else
                writes++;
        }

        destination.Add(Stat(string.Create(Invariant, $"ops {opLines.Count} (reads {reads} writes {writes})")));
        destination.AddRange(opLines);
        if (interleavedFrames.Count == 0)
            return;
        destination.Add(Stat("interleaved"));
        Render(destination, Options with
        {
            Level = TextLevel.Frames,
            Collapse = false,
            Header = false,
            Filter = TextFilter.All with { Match = r => r.Kind == TextRecordKind.Frame && interleavedFrames.Contains(r.Frame), MatchKey = "trace" },
        });
    }
}
