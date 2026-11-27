using System.Buffers;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.Core.Text;

public sealed partial class TextRenderer
{
    private static readonly string[] TypeNames = new string[256];

    /// <summary>The stable ID of an invocation: `#` and its wire ID, with `.n` for the n-th reuse of that ID (`#5.2`).</summary>
    /// <param name="invocation">Index into the assembler's invocations. Call under the session's gate.</param>
    public string InvocationId(int invocation)
    {
        var rows = _session.Assembler.Invocations;
        while (_occurrence.Count <= invocation && _occurrence.Count < rows.Length)
        {
            var id = rows[_occurrence.Count].InvocationId;
            var n = _idCounts.GetValueOrDefault(id) + 1;
            _idCounts[id] = n;
            _occurrence.Add(n);
        }

        var occurrence = invocation < _occurrence.Count ? _occurrence[invocation] : 1;
        var wire = rows[invocation].InvocationId;
        return occurrence > 1
            ? string.Create(Invariant, $"#{wire}.{occurrence}")
            : string.Create(Invariant, $"#{wire}");
    }

    /// <summary>The stable ID of a pipe: its glyph (≋ channel, ☰ collection, ⇄ raw) and `client·server` IDs.</summary>
    /// <param name="pipe">Index into the assembler's pipes. Call under the session's gate.</param>
    public string PipeId(int pipe)
    {
        var row = _session.Assembler.Pipes[pipe];
        var glyph = row.Content switch
        {
            PipeContent.Channel => "≋",
            PipeContent.Collection => "☰",
            _ => "⇄",
        };
        return string.Create(Invariant, $"{glyph}{row.PipeId & 0xFF}·{row.PipeId >> 8}");
    }

    /// <summary>The stable ID of a frame: `f` and its 1-based position in the session's frame order.</summary>
    /// <param name="orderIndex">Index into <see cref="CaptureSession.FrameOrder"/>.</param>
    public static string FrameId(int orderIndex) => string.Create(Invariant, $"f{orderIndex + 1}");

    /// <summary>A frame type's name (`Invocation`, `DuplexPipeWrite`, …), cached.</summary>
    public static string TypeName(MessageType type)
    {
        var name = TypeNames[(byte)type];
        if (name is null)
        {
            name = type.ToString();
            TypeNames[(byte)type] = name;
        }

        return name;
    }

    /// <summary>The name an invocation calls: the method, the collection it connects to, `m12`, or `?`.</summary>
    /// <param name="invocation">The invocation. Call under the session's gate.</param>
    public string MethodName(in InvocationRow invocation)
    {
        var decoder = _session.Decoder;
        if (decoder.TryGetMethod(invocation, out var method))
            return method.Name;
        if (invocation.RequestParsed && decoder.IsClientCall(invocation) != false && decoder.TryGetCollection(invocation.MethodId, out var collection))
            return collection.Name;
        return invocation.RequestParsed ? string.Create(Invariant, $"m{invocation.MethodId}") : "?";
    }

    private void Header()
    {
        var open = _session.Open;
        _sb.Append(open.Role == NntlSessionRole.Server ? "server " : "client ");
        _sb.Append(open.TransportName.ToLowerInvariant()).Append(' ');
        _sb.Append(string.IsNullOrEmpty(open.RemoteEndpoint) ? "-" : open.RemoteEndpoint);
        _sb.Append(" schema ");
        var resolution = _session.Schema;
        string name = resolution.Schema is null ? "none" : _session.Decoder.Export?.Name ?? "?";
        _sb.Append(name);
        if (SchemaHash.TryRead(open.SchemaHash.Span, out var hash))
            _sb.Append(Invariant, $"#{(uint)(hash >> 32):x8}");
        if (resolution.Status == SchemaStatus.Mismatch)
            _sb.Append(" (mismatch)");
        _sb.Append(Invariant, $" t0={_session.Header.ToUtc(_session.StartTicks):yyyy-MM-ddTHH:mm:ss.fffZ}");
        Emit(NewRecord(TextRecordKind.Header, _session.StartTicks, null) with
        {
            Id = string.Create(Invariant, $"@0x{open.SessionId:X}"),
            Value = Text(),
        });
    }

    private void OnGap(in NntlGap gap)
    {
        Emit(NewRecord(TextRecordKind.Gap, gap.FromTicks, gap.Direction) with
        {
            Value = string.Create(Invariant, $"{gap.DroppedOps} ops"),
            Bytes = gap.DroppedBytes,
            LastTicks = gap.ToTicks,
            SpanMs = Span(gap.FromTicks, gap.ToTicks),
        });
    }

    private void End()
    {
        var close = _session.Close;
        string reason;
        if (close is { Kind: NntlCloseKind.Disconnected } disconnected)
            reason = disconnected.DisconnectReason.ToString();
        else if (close is not null || _session.State == CaptureState.Stopped)
            reason = "stopped";
        else
            reason = "truncated";

        Emit(NewRecord(TextRecordKind.End, close?.Ticks ?? _session.EndTicks, null) with
        {
            Outcome = reason,
            Name = DisconnectSide(),
        });
    }

    // The side whose disconnect frame came last: the tapped endpoint's role for an outbound one.
    private string? DisconnectSide()
    {
        var order = _session.FrameOrder;
        for (var i = order.Length - 1; i >= 0 && i >= order.Length - 64; i--)
        {
            if (!_session.TryGetFrame(order[i], out var frame) || frame.Kind != FrameKind.Frame || !IsDisconnect(frame.Type))
                continue;
            var tappedIsServer = _session.Role == NntlSessionRole.Server;
            var senderIsServer = frame.Direction == TapDirection.Outbound ? tappedIsServer : !tappedIsServer;
            return senderIsServer ? "server" : "client";
        }

        return null;
    }

    private static bool IsDisconnect(MessageType type) => (byte)type is >= 20 and <= 39;

    private static bool IsNoise(MessageType type) => type is MessageType.Ping or MessageType.InvocationCredit or MessageType.PipeWindowUpdate;

    private void OnFrame(int orderIndex, FrameKey key)
    {
        if (!_session.TryGetFrame(key, out var frame))
            return;

        if (Options.Level >= TextLevel.Frames)
        {
            FrameLine(orderIndex, frame);
            return;
        }

        if (frame.Kind == FrameKind.Preamble)
            return;
        if (frame.Kind == FrameKind.Malformed)
        {
            Problem(orderIndex, frame, "malformed");
            return;
        }

        if ((frame.Flags & FrameFlags.Incomplete) != 0)
        {
            Problem(orderIndex, frame, TypeName(frame.Type) + " incomplete");
            return;
        }

        var assembler = _session.Assembler;
        var link = assembler.GetLink(frame.Direction, frame.Seq);
        if ((link.Flags & FrameLinkFlags.BodyMalformed) != 0)
        {
            Problem(orderIndex, frame, TypeName(frame.Type) + " body malformed");
            return;
        }

        switch (frame.Type)
        {
            case MessageType.Ping:
            case MessageType.InvocationCredit:
            case MessageType.PipeWindowUpdate:
                Noise(orderIndex, frame, link);
                return;
            case MessageType.ServerGreeting:
                Handshake(frame);
                return;
            case MessageType.ClientGreeting:
            case MessageType.ClientGreetingReconnection:
                if (!HasServerGreeting() && _session.State != CaptureState.Open)
                    Handshake(frame);
                return;
            case MessageType.Invocation:
            case MessageType.MessageStart:
            case MessageType.InvocationResult:
            case MessageType.InvocationCancellation:
                if (link.Entity == EntityKind.Invocation && (uint)link.EntityIndex < (uint)assembler.Invocations.Length)
                    InvocationFrame(orderIndex, frame, link.EntityIndex);
                return;
            case MessageType.DuplexPipeUpdateState:
                if (link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length)
                    PipeState(orderIndex, frame, link.EntityIndex);
                return;
            case MessageType.DuplexPipeWrite:
                if (link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length)
                    PipeData(orderIndex, frame, link);
                return;
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

    private void Problem(int orderIndex, in FrameRecord frame, string what)
    {
        _sb.Append(what).Append(' ');
        TextValues.AppendSize(_sb, frame.Length);
        Emit(NewRecord(TextRecordKind.Problem, frame.StartTicks, frame.Direction) with
        {
            Id = FrameId(orderIndex),
            Value = Text(),
            Frame = orderIndex,
            Bytes = frame.Length,
        });
    }

    private void Noise(int orderIndex, in FrameRecord frame, in FrameLink link)
    {
        var assembler = _session.Assembler;
        string word;
        string? detail = null;
        switch (frame.Type)
        {
            case MessageType.Ping:
                word = "ping";
                break;
            case MessageType.InvocationCredit:
                word = "credit";
                var credit = assembler.GetDetail(frame.Direction, frame.Seq);
                if (credit.Kind == FrameDetailKind.Credit)
                    detail = string.Create(Invariant, $"+{credit.Value}");
                break;
            default:
                word = "window";
                if (link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length)
                    detail = PipeId(link.EntityIndex);
                break;
        }

        Emit(NewRecord(TextRecordKind.Noise, frame.StartTicks, frame.Direction) with
        {
            Name = word,
            Value = detail is null ? word : word + " " + detail,
            Frame = orderIndex,
        });
    }

    private void Handshake(in FrameRecord at)
    {
        var assembler = _session.Assembler;
        var greetings = assembler.Greetings;
        var server = -1;
        var client = -1;
        for (var i = 0; i < greetings.Length; i++)
        {
            if (greetings[i].Kind == GreetingKind.Server)
                server = i;
            else if (client < 0)
                client = i;
        }

        var version = PreambleVersion();
        if (version >= 0)
            _sb.Append(Invariant, $"v{version}");

        if (client >= 0)
        {
            var greeting = greetings[client];
            if (greeting.Kind == GreetingKind.ClientReconnection)
                Word("reconnect");
            Word("token=");
            AppendToken(greeting);
            if (greeting.Version is { } clientVersion)
            {
                Word("ver=");
                _values.AppendCut(_sb, clientVersion);
            }
        }

        if (server >= 0)
            Word(string.Create(Invariant, $"id={greetings[server].ClientId}"));

        Emit(NewRecord(TextRecordKind.Handshake, at.StartTicks, null) with
        {
            Outcome = server >= 0 ? "ok" : "fail",
            Value = Text(),
        });
    }

    // A capture that joined the session mid-stream saw no handshake.
    private void Attached(FrameKey first)
    {
        var ticks = _session.TryGetFrame(first, out var frame) ? frame.StartTicks : _session.StartTicks;
        Emit(NewRecord(TextRecordKind.Handshake, ticks, null) with { Outcome = "none", Value = "(attached mid-stream)" });
    }

    private void Word(string word)
    {
        if (_sb.Length > 0)
            _sb.Append(' ');
        _sb.Append(word);
    }

    private long PreambleVersion()
    {
        var assembler = _session.Assembler;
        foreach (var direction in (ReadOnlySpan<TapDirection>)[TapDirection.Outbound, TapDirection.Inbound])
        {
            var frames = assembler.GetFrames(direction);
            if (frames.Length > 0 && frames[0].Kind == FrameKind.Preamble)
            {
                var detail = assembler.GetDetail(direction, frames[0].Seq);
                if (detail.Kind == FrameDetailKind.Preamble)
                    return detail.Value;
            }
        }

        return -1;
    }

    // The token as the source holds it: a redaction marker shows as its hash prefix; a raw token shows cut, since the
    // recording kept it (InspectorOptions.IncludeSecrets).
    private void AppendToken(in GreetingRow greeting)
    {
        if (greeting.Token.IsNone)
        {
            _sb.Append("none");
            return;
        }

        if (greeting.Token.Length == 0)
        {
            _sb.Append("empty");
            return;
        }

        var assembler = _session.Assembler;
        var redacted = assembler.TryGetFrame(greeting.Direction, greeting.Seq, out var frame) && (frame.Flags & FrameFlags.Redacted) != 0;
        Span<byte> head = stackalloc byte[Math.Min(64, (int)Math.Min(int.MaxValue, greeting.Token.Length))];
        var read = assembler.TryGetPayload(greeting.Token with { Length = head.Length }, out var bytes);
        if (read)
            bytes.CopyTo(head);

        if (redacted)
        {
            _sb.Append("redacted(sha256:");
            if (read)
            {
                foreach (var b in head[..Math.Min(8, head.Length)])
                    _sb.Append((char)b);
            }

            _sb.Append("…)");
            return;
        }

        if (!read)
        {
            TextValues.AppendSize(_sb, greeting.Token.Length);
            return;
        }

        _values.AppendQuotedUtf8(_sb, head);
        TextValues.AppendSize(_sb.Append(" ("), greeting.Token.Length).Append(')');
    }

    private void InvocationFrame(int orderIndex, in FrameRecord frame, int invocation)
    {
        var row = _session.Assembler.Invocations[invocation];
        var isRequest = row.HasRequest && row.Request.Direction == frame.Direction && row.Request.Seq == frame.Seq;
        var isResult = !row.Result.IsNone && row.Result.Direction == frame.Direction && row.Result.Seq == frame.Seq;
        if (isRequest)
        {
            Call(orderIndex, frame, invocation, row);
            return;
        }

        if (isResult && (!row.HasRequest || _pendingShown.Remove(invocation)))
        {
            CallResult(orderIndex, frame, invocation, row);
            return;
        }

        if (frame.Type == MessageType.InvocationCancellation && _pendingShown.Contains(invocation))
        {
            Emit(NewRecord(TextRecordKind.Cancel, frame.StartTicks, frame.Direction) with
            {
                Id = InvocationId(invocation),
                Frame = orderIndex,
                Entity = invocation,
            });
        }
    }

    private void Call(int orderIndex, in FrameRecord frame, int invocation, in InvocationRow row)
    {
        var name = MethodName(row);
        var arguments = Arguments(row);
        var outcome = Outcome(row, out var result, out var end);
        if (row.State == InvocationState.Pending)
            _pendingShown.Add(invocation);

        var record = NewRecord(TextRecordKind.Call, frame.StartTicks, frame.Direction) with
        {
            Id = InvocationId(invocation),
            Name = name,
            Value = arguments,
            Outcome = outcome,
            Result = result,
            DurationMs = end > 0 ? Span(frame.StartTicks, end) : null,
            Frame = orderIndex,
            Entity = invocation,
            Bytes = RequestBytes(row),
        };
        _sb.Append(frame.Direction == TapDirection.Outbound ? '>' : '<').Append(name).Append('(').Append(arguments).Append(')')
            .Append(outcome).Append('|').Append(result);
        Emit(record with { Key = Text() });
    }

    private void CallResult(int orderIndex, in FrameRecord frame, int invocation, in InvocationRow row)
    {
        var outcome = Outcome(row, out var result, out var end);
        double? duration = null;
        if (row.HasRequest && end > 0 && _session.Assembler.TryGetFrame(row.Request.Direction, row.Request.Seq, out var request))
            duration = Span(request.StartTicks, end);
        Emit(NewRecord(TextRecordKind.Result, frame.StartTicks, frame.Direction) with
        {
            Id = InvocationId(invocation),
            Name = MethodName(row),
            Outcome = outcome ?? "…",
            Result = result,
            DurationMs = duration,
            Frame = orderIndex,
            Entity = invocation,
        });
    }

    private long RequestBytes(in InvocationRow row)
    {
        var assembler = _session.Assembler;
        if (row.Request.Kind == MessageRefKind.Assembled && (uint)row.Request.Index < (uint)assembler.Messages.Length)
        {
            var message = assembler.Messages[row.Request.Index];
            return message.TotalLength >= 0 ? message.TotalLength : message.Length;
        }

        return row.Arguments.IsNone ? -1 : row.Arguments.Length;
    }

    // The argument words: decoded values, or a large message's size, chunks and transport splits.
    private string Arguments(in InvocationRow row)
    {
        if (!row.HasRequest)
            return "?";

        var assembler = _session.Assembler;
        if (row.Request.Kind == MessageRefKind.Assembled && (uint)row.Request.Index < (uint)assembler.Messages.Length)
        {
            LargeMessage(assembler.Messages[row.Request.Index]);
            return Text();
        }

        var decoded = _session.Decoder.DecodeArguments(row);
        _values.AppendArguments(_sb, decoded);
        return Text();
    }

    // `18.4KB, 5 chunks, T×3` and how far it got.
    private void LargeMessage(in AssembledMessage message)
    {
        TextValues.AppendSize(_sb, message.TotalLength >= 0 ? message.TotalLength : message.Length);
        var chunks = 0;
        var splits = 0;
        _fragments.Clear();
        var assembler = _session.Assembler;
        assembler.GetFragments(message.Ref, _fragments);
        foreach (var fragment in _fragments)
        {
            if (!assembler.TryGetFrame(fragment.Direction, fragment.Seq, out var frame))
                continue;
            if (frame.Type == MessageType.MessageChunk)
                chunks++;
            if ((frame.Flags & FrameFlags.TransportSplit) != 0)
                splits++;
        }

        _fragments.Clear();
        _sb.Append(Invariant, $", {chunks} chunks");
        if (splits > 0)
            _sb.Append(Invariant, $", T×{splits}");
        switch (message.State)
        {
            case AssemblyState.Assembling:
                _sb.Append(", …");
                break;
            case AssemblyState.Aborted:
                _sb.Append(", aborted");
                break;
            case AssemblyState.Truncated:
                _sb.Append(", truncated");
                break;
            case AssemblyState.Orphan:
                _sb.Append(", orphan");
                break;
        }
    }

    // The outcome word, the result's words and the end ticks of the result (0 when none).
    private string? Outcome(in InvocationRow row, out string? result, out long end)
    {
        result = null;
        end = ResultEnd(row);
        switch (row.State)
        {
            case InvocationState.OneWay:
                return "oneway";
            case InvocationState.Pending:
                return row.CancellationSeq >= 0 ? "… cancel sent" : "…";
            case InvocationState.Completed:
                result = ResultValue(row);
                return "ok";
            case InvocationState.Exception:
                result = ResultValue(row);
                return "exception";
            case InvocationState.Canceled:
                return "canceled";
            case InvocationState.Unauthorized:
                return "unauthorized";
            case InvocationState.Refused:
                return "refused";
            default:
                return null;
        }
    }

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

    private string? ResultValue(in InvocationRow row)
    {
        var assembler = _session.Assembler;
        if (row.Result.Kind == MessageRefKind.Assembled && (uint)row.Result.Index < (uint)assembler.Messages.Length)
        {
            LargeMessage(assembler.Messages[row.Result.Index]);
            return Text();
        }

        if (row.Value.IsNone)
            return null;
        var decoded = _session.Decoder.DecodeResult(row);
        if (decoded.Fields.Length == 0)
            return null;
        if (decoded.Fields[0].Kind == FieldKind.ExceptionDetail)
            _values.AppendException(_sb, decoded, 0);
        else
            _values.AppendTop(_sb, decoded);
        return Text();
    }

    private void PipeState(int orderIndex, in FrameRecord frame, int pipe)
    {
        var assembler = _session.Assembler;
        var detail = assembler.GetDetail(frame.Direction, frame.Seq);
        var flags = detail.Kind == FrameDetailKind.PipeState ? (PipeStateFlags)detail.Value : PipeStateFlags.None;
        var row = assembler.Pipes[pipe];
        if ((flags & PipeStateFlags.Refused) != 0)
        {
            _sb.Append("refused");
        }
        else if ((flags & PipeStateFlags.Complete) == PipeStateFlags.Complete)
        {
            _sb.Append("complete");
        }
        else if ((flags & (PipeStateFlags.ClientWriterServerReaderComplete | PipeStateFlags.ClientReaderServerWriterComplete)) != 0)
        {
            _sb.Append((flags & PipeStateFlags.ClientWriterServerReaderComplete) != 0 ? "client done" : "server done");
        }
        else
        {
            _sb.Append("open");
            if (_session.Decoder.GetPipeLabel(pipe) is { } label)
                _sb.Append(' ').Append(label);
            if (row.OpenerInvocation >= 0 && (uint)row.OpenerInvocation < (uint)assembler.Invocations.Length)
                _sb.Append(" by ").Append(InvocationId(row.OpenerInvocation));
        }

        Emit(NewRecord(TextRecordKind.Pipe, frame.StartTicks, frame.Direction) with
        {
            Id = PipeId(pipe),
            Value = Text(),
            Frame = orderIndex,
            Entity = pipe,
        });
    }

    private void PipeData(int orderIndex, in FrameRecord frame, in FrameLink link)
    {
        var assembler = _session.Assembler;
        var pipe = link.EntityIndex;
        var row = assembler.Pipes[pipe];
        if (row.Content != PipeContent.Raw && Items(orderIndex, frame, pipe))
            return;
        if (row.Content != PipeContent.Raw && !row.ChannelUnavailable)
            return;

        // Raw data (or a channel whose items cannot be split): one line per run, at its first frame.
        if ((uint)link.Message >= (uint)assembler.Messages.Length)
            return;
        var run = assembler.Messages[link.Message];
        if (run.FirstSeq != frame.Seq)
            return;

        var id = PipeId(pipe);
        Emit(NewRecord(TextRecordKind.Data, frame.StartTicks, frame.Direction) with
        {
            Id = id,
            Value = row.Content == PipeContent.Raw ? null : "items unavailable",
            Bytes = run.Length,
            Frame = orderIndex,
            Entity = pipe,
            Key = (frame.Direction == TapDirection.Outbound ? ">" : "<") + id,
        });
    }

    // The items of a channel or collection pipe that start in this frame. True when there were any.
    private bool Items(int orderIndex, in FrameRecord frame, int pipe)
    {
        var assembler = _session.Assembler;
        var list = assembler.GetPipeItems(pipe, frame.Direction);
        var rows = assembler.ChannelItems;
        var key = ((long)pipe << 1) | (long)frame.Direction;
        if (!_itemNext.TryGetValue(key, out var next))
        {
            next = 0;
            while (next < list.Length && rows[list[next]].FirstSeq < frame.Seq)
                next++;
        }

        var any = false;
        while (next < list.Length && rows[list[next]].FirstSeq <= frame.Seq)
        {
            var item = rows[list[next++]];
            if (item.FirstSeq == frame.Seq)
            {
                Item(orderIndex, frame, pipe, item);
                any = true;
            }
        }

        _itemNext[key] = next;
        return any;
    }

    private void Item(int orderIndex, in FrameRecord frame, int pipe, in ChannelItemRow item)
    {
        var assembler = _session.Assembler;
        var decoder = _session.Decoder;
        var id = PipeId(pipe);
        var label = decoder.GetPipeLabel(pipe);
        var arrow = frame.Direction == TapDirection.Outbound ? ">" : "<";
        if (assembler.Pipes[pipe].Content == PipeContent.Collection && (uint)item.CollectionMessage < (uint)assembler.CollectionMessages.Length)
        {
            var message = assembler.CollectionMessages[item.CollectionMessage];
            var kind = message.Malformed ? "malformed" : message.Kind.ToString();
            _sb.Append(kind);
            if (message.Version >= 0)
                _sb.Append(Invariant, $" v{message.Version}");
            if (message.Position >= 0)
                _sb.Append(Invariant, $" @{message.Position}");
            if (message.ToPosition >= 0)
                _sb.Append(Invariant, $"→{message.ToPosition}");
            if (message.TotalValues >= 0)
                _sb.Append(Invariant, $" n={message.TotalValues}");
            CollectionValue(message);
            Emit(NewRecord(TextRecordKind.Collection, frame.StartTicks, frame.Direction) with
            {
                Id = id,
                Name = label,
                Value = Text(),
                Bytes = item.Length,
                Frame = orderIndex,
                Entity = item.Index,
                Key = arrow + id + " " + kind,
            });
            return;
        }

        if (item.State is AssemblyState.Complete or AssemblyState.Assembling or AssemblyState.Truncated)
            _values.AppendTop(_sb, decoder.DecodeChannelItem(item));
        if (item.State != AssemblyState.Complete)
            _sb.Append(_sb.Length > 0 ? " " : "").Append(item.State.ToString().ToLowerInvariant());
        Emit(NewRecord(TextRecordKind.Item, frame.StartTicks, frame.Direction) with
        {
            Id = id,
            Name = label,
            Value = Text(),
            Bytes = item.Length,
            Frame = orderIndex,
            Entity = item.Index,
            Key = arrow + id,
        });
    }

    // The value(s) a collection message carries.
    private void CollectionValue(in CollectionMessageRow message)
    {
        if (message.Value.IsNone)
            return;
        var decoded = _session.Decoder.DecodeCollectionMessage(message);
        var fields = decoded.Fields;
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Kind is FieldKind.CollectionBody or FieldKind.CollectionMessage)
                continue;
            if (fields[i].Name is not ("Value" or "Values"))
                continue;
            _sb.Append(' ');
            _values.AppendField(_sb, decoded, i, 1);
            return;
        }
    }

    private void FrameLine(int orderIndex, in FrameRecord frame)
    {
        var assembler = _session.Assembler;
        var link = frame.Kind == FrameKind.Frame ? assembler.GetLink(frame.Direction, frame.Seq) : FrameLink.None;
        if (Options.Collapse && frame.Kind == FrameKind.Frame && (frame.Flags & FrameFlags.Incomplete) == 0 && IsNoise(frame.Type))
        {
            Noise(orderIndex, frame, link);
            return;
        }

        string name = frame.Kind switch
        {
            FrameKind.Preamble => "Preamble",
            FrameKind.Malformed => "Malformed",
            _ => TypeName(frame.Type),
        };

        // Target: the entity the frame belongs to.
        switch (link.Entity)
        {
            case EntityKind.Invocation when (uint)link.EntityIndex < (uint)assembler.Invocations.Length:
                _sb.Append(InvocationId(link.EntityIndex)).Append(' ').Append(MethodName(assembler.Invocations[link.EntityIndex]));
                break;
            case EntityKind.Pipe when (uint)link.EntityIndex < (uint)assembler.Pipes.Length:
                _sb.Append(PipeId(link.EntityIndex));
                if (_session.Decoder.GetPipeLabel(link.EntityIndex) is { } label)
                    _sb.Append(' ').Append(label);
                break;
        }

        DetailWords(assembler.GetDetail(frame.Direction, frame.Seq), frame.Flags);
        var value = Text();

        if ((frame.Flags & FrameFlags.TransportSplit) != 0)
            _sb.Append('T');
        if ((frame.Flags & FrameFlags.NexNetSplit) != 0)
            _sb.Append('N');
        if ((frame.Flags & FrameFlags.Incomplete) != 0)
            _sb.Append('…');
        if ((frame.Flags & FrameFlags.Cancel) != 0)
            _sb.Append('X');
        if ((frame.Flags & FrameFlags.Redacted) != 0)
            _sb.Append('R');
        if ((link.Flags & FrameLinkFlags.BodyMalformed) != 0)
            _sb.Append('!');
        var flags = _sb.Length > 0 ? Text() : null;

        Emit(NewRecord(TextRecordKind.Frame, frame.StartTicks, frame.Direction) with
        {
            Id = FrameId(orderIndex),
            Name = name,
            Value = value.Length > 0 ? value : null,
            Outcome = flags,
            Bytes = frame.Length,
            Frame = orderIndex,
            Entity = link.Entity == EntityKind.None ? -1 : link.EntityIndex,
        });

        if (Options.Level == TextLevel.Bytes && frame.Length > 0)
            Hex(orderIndex, frame);
    }

    private void DetailWords(in FrameDetail detail, FrameFlags flags)
    {
        var start = _sb.Length;
        switch (detail.Kind)
        {
            case FrameDetailKind.Preamble:
                _sb.Append(Invariant, $"v{detail.Value}");
                break;
            case FrameDetailKind.AuthToken:
                _sb.Append((flags & FrameFlags.Redacted) != 0 ? "token redacted"
                    : detail.Value < 0 ? "no token"
                    : string.Create(Invariant, $"token {detail.Value}B"));
                break;
            case FrameDetailKind.ClientId:
                _sb.Append(Invariant, $"id={detail.Value}");
                break;
            case FrameDetailKind.Credit:
                _sb.Append(Invariant, $"+{detail.Value}");
                break;
            case FrameDetailKind.PipeState:
                var state = (PipeStateFlags)detail.Value;
                _sb.Append((state & PipeStateFlags.Refused) != 0 ? "refused"
                    : (state & PipeStateFlags.Complete) == PipeStateFlags.Complete ? "complete"
                    : (state & PipeStateFlags.ClientWriterServerReaderComplete) != 0 ? "client done"
                    : (state & PipeStateFlags.ClientReaderServerWriterComplete) != 0 ? "server done"
                    : "ready");
                break;
            case FrameDetailKind.Result:
                _sb.Append((InvocationResultState)detail.Value switch
                {
                    InvocationResultState.CompletedResult => "ok",
                    InvocationResultState.Exception => "exception",
                    InvocationResultState.Unauthorized => "unauthorized",
                    InvocationResultState.Canceled => "canceled",
                    _ => "?",
                });
                break;
            case FrameDetailKind.MessageStart:
                _sb.Append((LargeMessageKind)detail.Value == LargeMessageKind.Result ? "result " : "arg ");
                TextValues.AppendSize(_sb, detail.Extra);
                break;
            default:
                return;
        }

        if (start > 0)
            _sb.Insert(start, ' ');
    }

    private void Hex(int orderIndex, in FrameRecord frame)
    {
        var limit = Math.Max(1, Options.ByteLimit);
        var shown = Math.Min(frame.Length, limit);
        Span<byte> bytes = shown <= 256 ? stackalloc byte[shown] : new byte[shown];
        if (_session.GetStore(frame.Direction).TryCopyTo(frame.Offset, bytes))
        {
            foreach (var b in bytes)
                _sb.Append(Invariant, $"{b:x2}");
        }
        else
        {
            _sb.Append("(bytes no longer held)");
        }

        if (shown < frame.Length)
            TextValues.AppendSize(_sb.Append("…+"), frame.Length - shown);
        Emit(NewRecord(TextRecordKind.Bytes, frame.StartTicks, frame.Direction) with
        {
            Id = FrameId(orderIndex),
            Value = Text(),
            Bytes = frame.Length,
            Frame = orderIndex,
        });
    }
}
