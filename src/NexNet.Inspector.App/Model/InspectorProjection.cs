using System.Globalization;
using NexNet.Inspector.App.Controls.PropertyTree;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.App.Model;

/// <summary>The footer of the Decoded tab: how the selected frame's message was assembled.</summary>
/// <param name="Title">"Invocation #812", "Pipe 3·1 write", ….</param>
/// <param name="Summary">"18,422 B · 5 chunks · 15.0 ms".</param>
/// <param name="Note">"Chunk 2 arrived in reads #4471 + #4472 · 6 frames interleaved".</param>
/// <param name="Segments">One per fragment, in message order.</param>
public sealed record AssemblyInfo(string Title, string Summary, string Note, AssemblySegment[] Segments);

/// <summary>What the inspector shows for one frame.</summary>
/// <param name="Frame">The frame.</param>
/// <param name="Message">The message the frame carries (a part of).</param>
/// <param name="Tree">The Decoded tab's tree.</param>
/// <param name="FrameLabel">The header's frame id ("#18,233").</param>
/// <param name="IsHandshake">True for the preamble and greetings: the second tab reads "Handshake".</param>
/// <param name="Assembly">The assembly footer; null for a message carried by one frame.</param>
/// <param name="IsFinal">
/// False while more data can change what is shown (the frame or its message still arriving, an invocation waiting for its
/// result, a pipe still open): a live document rebuilds the content as data arrives only then.
/// </param>
public sealed record InspectorContent(FrameKey Frame, MessageRef Message, PropertyTreeModel Tree, string FrameLabel, bool IsHandshake, AssemblyInfo? Assembly,
    bool IsFinal);

/// <summary>
/// Builds the inspector's content for a frame from a capture session: the Frame header (type, length, post-header
/// ids with their wire bytes), the Transport ops that carried it, the entity it belongs to (invocation, pipe, greeting)
/// with its decoded arguments, result or item (schema-driven; raw without a schema, OQ3), and the assembly bar's
/// fragments. Call it under the session's gate.
/// </summary>
public static class InspectorProjection
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Builds the content; null for a frame the session does not hold.</summary>
    public static InspectorContent? Build(CaptureSession session, FrameKey key)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.TryGetFrame(key, out FrameRecord frame)) return null;

        MessageAssembler assembler = session.Assembler;
        TransportOpStore store = session.GetStore(frame.Direction);
        PayloadMap frameMap = PayloadMap.ForFrame(frame.Direction, frame.Seq, frame.BodyOffset);
        PropertyTreeModel tree = new();
        FrameLink link = assembler.GetLink(frame.Direction, frame.Seq);

        Header(tree, session, frame, store, frameMap);
        Transport(tree, frame, store, frameMap);

        MessageRef message = assembler.GetMessage(frame.Direction, frame.Seq);
        TraceFragment[] fragments = [];
        if (link.Message >= 0)
        {
            List<TraceFragment> list = [];
            assembler.GetFragments(assembler.Messages[link.Message].Ref, list);
            fragments = [.. list];
        }

        switch (link.Entity)
        {
            case EntityKind.Invocation:
                Invocation(tree, session, frame, link, fragments);
                break;
            case EntityKind.Pipe:
                Pipe(tree, session, frame, link, fragments);
                break;
            case EntityKind.Greeting:
                Greeting(tree, assembler.Greetings[link.EntityIndex], store, frameMap);
                break;
            default:
                Body(tree, frame, store, frameMap);
                break;
        }

        tree.Complete();
        bool handshake = frame.Kind == FrameKind.Preamble || link.Entity == EntityKind.Greeting;
        AssemblyInfo? assembly = link.Message >= 0 ? Assembly(session, frame, assembler.Messages[link.Message], fragments) : null;
        bool final = (frame.Flags & FrameFlags.Incomplete) == 0
                     && (link.Message < 0 || assembler.Messages[link.Message].State != AssemblyState.Assembling)
                     && link.Entity switch
                     {
                         EntityKind.Invocation => assembler.Invocations[link.EntityIndex].State != InvocationState.Pending,
                         EntityKind.Pipe => assembler.Pipes[link.EntityIndex].Lifecycle is PipeLifecycle.Closed or PipeLifecycle.Refused,
                         _ => true,
                     };
        return new InspectorContent(key, message, tree, "#" + frame.Seq.ToString("N0", Invariant), handshake, assembly, final);
    }

    private static void Header(PropertyTreeModel tree, CaptureSession session, in FrameRecord frame, TransportOpStore store, in PayloadMap map)
    {
        double ms = (frame.StartTicks - session.StartTicks) * 1000.0 / session.Header.StopwatchFrequency;
        tree.Section("Frame header", Word(frame.Direction) + " · " + ms.ToString("N3", Invariant) + " ms");
        long toBody = frame.Offset - frame.BodyOffset;
        Span<byte> wire = stackalloc byte[3];

        if (frame.Kind == FrameKind.Preamble)
        {
            tree.Field("Type", "Preamble · " + frame.Length.ToString(Invariant) + " B", bytes: map.Map(toBody, frame.Length));
            return;
        }

        string type = frame.Kind == FrameKind.Malformed ? "malformed" : frame.Type.ToString();
        tree.Field("Type", "0x" + ((byte)frame.Type).ToString("X2", Invariant) + " · " + type, bytes: map.Map(toBody, 1));
        if (frame.HeaderLength >= 3)
        {
            string le = Read(store, frame.Offset + 1, wire[..2]) ? " · LE " + Hex(wire[..2]) : "";
            tree.Field("Body length", frame.BodyLength.ToString("N0", Invariant) + le, bytes: map.Map(toBody + 1, 2));
        }

        long post = toBody + frame.HeaderLength;
        long postStream = frame.Offset + frame.HeaderLength;
        switch (frame.Target)
        {
            case FrameTarget.Invocation:
            {
                string le = Read(store, postStream, wire[..2]) ? " · LE " + Hex(wire[..2]) : "";
                tree.Field("Invocation", frame.TargetId.ToString(Invariant) + le, bytes: map.Map(post, 2));
                if (frame.PostHeaderLength >= 3)
                    tree.Field("Kind", frame.ChunkKind.ToString(Invariant) + " · " + (frame.ChunkKind == 0 ? "Argument" : "Result"), bytes: map.Map(post + 2, 1));
                break;
            }
            case FrameTarget.Pipe:
            {
                string le = Read(store, postStream, wire[..2]) ? " · LE " + Hex(wire[..2]) : "";
                tree.Field("Pipe", PipeName(frame.TargetId) + le, bytes: map.Map(post, 2));
                break;
            }
        }
    }

    private static void Transport(PropertyTreeModel tree, in FrameRecord frame, TransportOpStore store, in PayloadMap map)
    {
        bool reads = frame.Direction == TapDirection.Inbound;
        long ops = frame.LastOpId - frame.FirstOpId + 1;
        string unit = reads ? (ops == 1 ? "read" : "reads") : (ops == 1 ? "write" : "writes");
        tree.Section("Transport", ops == 1 ? "1 " + unit : "split across " + ops.ToString(Invariant) + " " + unit);

        long end = frame.Offset + frame.Length;
        if (!store.TryFindOp(frame.Offset, out int index))
        {
            tree.Field("#" + frame.FirstOpId.ToString(Invariant), "bytes no longer held", PropertyTone.Muted);
            return;
        }

        ReadOnlySpan<StreamOp> all = store.Ops;
        for (int i = index; i < all.Length && all[i].Offset < end; i++)
        {
            StreamOp op = all[i];
            long from = Math.Max(op.Offset, frame.Offset);
            long to = Math.Min(op.End, end);
            if (to <= from) continue;
            tree.Field("#" + op.OpId.ToString(Invariant), from.ToString("X6", Invariant) + " – " + (to - 1).ToString("X6", Invariant),
                PropertyTone.Transport, map.Map(from - frame.BodyOffset, (int)(to - from)));
        }
    }

    private static void Invocation(PropertyTreeModel tree, CaptureSession session, in FrameRecord frame, in FrameLink link, TraceFragment[] fragments)
    {
        MessageAssembler assembler = session.Assembler;
        SessionDecoder decoder = session.Decoder;
        InvocationRow invocation = assembler.Invocations[link.EntityIndex];
        bool request = frame.Direction == invocation.Direction;
        AssembledMessage? large = link.Message >= 0 && assembler.Messages[link.Message].Kind == AssembledMessageKind.Large
            ? assembler.Messages[link.Message]
            : null;

        string title = invocation.State == InvocationState.OneWay
            ? "Invocation · one-way"
            : "Invocation #" + invocation.InvocationId.ToString(Invariant);
        string summary = large is { } m
            ? m.State == AssemblyState.Complete ? "reassembled" : Lower(m.State.ToString())
            : Lower(invocation.State.ToString());
        tree.Section(title, summary);

        if (large is not null)
        {
            int chunks = 0;
            bool start = false;
            foreach (TraceFragment f in fragments)
            {
                if (f.Length > 0) chunks++;
                else if (f.MessageOffset == 0 && !start) start = true;
            }

            string split = (start ? "Start + " : "") + chunks.ToString(Invariant) + (chunks == 1 ? " chunk" : " chunks");
            tree.Field("NexNet split", split, PropertyTone.NexNet);
        }

        if (decoder.TryGetMethod(invocation, out SchemaMethod? method))
        {
            string? owner = decoder.Export is { } export
                ? decoder.IsClientCall(invocation) switch { true => export.ServerApi.Name, false => export.ClientApi.Name, _ => null }
                : null;
            tree.Field("Method", owner is null ? method.Name : owner + "." + method.Name);
        }
        else if (invocation.RequestParsed)
        {
            tree.Field("Method", "id " + invocation.MethodId.ToString(Invariant) + (decoder.Schema is null ? " · no schema" : " · unknown"), PropertyTone.Muted);
        }

        DecodedValue value = request ? decoder.DecodeArguments(invocation) : decoder.DecodeResult(invocation);
        string rawName = request ? "Arguments" : "Value";
        if (value.Fields.IsEmpty)
        {
            if (request && !invocation.HasRequest) tree.Field(rawName, "not captured", PropertyTone.Muted);
        }
        else
        {
            tree.Decoded(value, MapOf(session, value.Range.Message, fragments), decoder.Schema is null ? rawName + " · raw" : rawName);
        }

        if (request && !invocation.Result.IsNone)
        {
            MessageRef result = invocation.Result;
            tree.Field("Result", "#" + result.Seq.ToString("N0", Invariant) + " " + Arrow(result.Direction) + " " + invocation.State);
        }
        else if (!request && invocation.HasRequest)
        {
            tree.Field("Request", "#" + invocation.Request.Seq.ToString("N0", Invariant) + " " + Arrow(invocation.Request.Direction));
        }

        if (invocation.CancellationSeq >= 0)
            tree.Field("Cancellation", "#" + invocation.CancellationSeq.ToString("N0", Invariant), PropertyTone.NexNet);
    }

    private static void Pipe(PropertyTreeModel tree, CaptureSession session, in FrameRecord frame, in FrameLink link, TraceFragment[] fragments)
    {
        MessageAssembler assembler = session.Assembler;
        PipeRow pipe = assembler.Pipes[link.EntityIndex];
        tree.Section("Pipe " + PipeName(pipe.PipeId), Lower(pipe.Content.ToString()) + " · " + Lower(pipe.Lifecycle.ToString()));

        if (link.Message >= 0 && fragments.Length > 1)
            tree.Field("NexNet split", fragments.Length.ToString(Invariant) + " writes", PropertyTone.NexNet);
        if (pipe.OpenerInvocation >= 0)
        {
            InvocationRow opener = assembler.Invocations[pipe.OpenerInvocation];
            string name = session.Decoder.TryGetMethod(opener, out SchemaMethod? method) ? " · " + method.Name : "";
            tree.Field("Opened by", "#" + opener.InvocationId.ToString(Invariant) + name);
        }

        tree.Field("Received", pipe.InboundBytes.ToString("N0", Invariant) + " B · " + pipe.InboundRuns.ToString(Invariant) + " runs");
        tree.Field("Sent", pipe.OutboundBytes.ToString("N0", Invariant) + " B · " + pipe.OutboundRuns.ToString(Invariant) + " runs");

        if (pipe.Content == PipeContent.Raw) return;

        // A typed channel or collection: the items whose bytes this frame carried.
        ReadOnlySpan<ChannelItemRow> items = assembler.ChannelItems;
        for (int i = 0; i < items.Length; i++)
        {
            ChannelItemRow item = items[i];
            if (item.Pipe != pipe.Index || item.Direction != frame.Direction || frame.Seq < item.FirstSeq || frame.Seq > item.LastSeq) continue;
            DecodedValue value = session.Decoder.DecodeChannelItem(item);
            if (value.Fields.IsEmpty) continue;
            List<TraceFragment> parts = [];
            assembler.GetFragments(item.Ref, parts);
            tree.Decoded(value, PayloadMap.ForFragments(item.Ref, [.. parts]), "Item");
        }
    }

    private static void Greeting(PropertyTreeModel tree, in GreetingRow greeting, TransportOpStore store, in PayloadMap map)
    {
        string title = greeting.Kind switch
        {
            GreetingKind.Client => "Client greeting",
            GreetingKind.ClientReconnection => "Client reconnection",
            _ => "Server greeting",
        };
        tree.Section(title, "handshake");
        if (greeting.Kind == GreetingKind.Server)
        {
            tree.Field("Client id", "0x" + greeting.ClientId.ToString("X", Invariant));
            tree.Field("Client nexus hash", "0x" + greeting.ClientNexusHash.ToString("X8", Invariant));
            if (greeting.IdleTimeoutMs > 0) tree.Field("Idle timeout", greeting.IdleTimeoutMs.ToString("N0", Invariant) + " ms");
        }
        else
        {
            if (greeting.Version is { } version) tree.Field("Version", "\"" + version + "\"");
            tree.Field("Server nexus hash", "0x" + greeting.ServerNexusHash.ToString("X8", Invariant));
            tree.Field("Client nexus hash", "0x" + greeting.ClientNexusHash.ToString("X8", Invariant));
            Token(tree, greeting.Token, store, map);
        }

        tree.Field("Max message size", greeting.MaxMessageSize.ToString("N0", Invariant) + " B");
        tree.Field("Invocation credits", greeting.InvocationCredits.ToString("N0", Invariant));
    }

    private static void Token(PropertyTreeModel tree, PayloadRange token, TransportOpStore store, in PayloadMap map)
    {
        if (token.IsNone)
        {
            tree.Field("authenticationToken", "nil", PropertyTone.Muted);
            return;
        }

        int length = (int)token.Length;
        PropertyBytes bytes = map.Map(token.Offset, length);
        bool redacted = false;
        foreach (StreamRange r in store.Redactions)
        {
            if (r.Offset < bytes.Stream.End && bytes.Stream.Offset < r.End)
            {
                redacted = true;
                break;
            }
        }

        string size = "bin " + length.ToString("N0", Invariant) + " B";
        if (!redacted)
        {
            tree.Field("authenticationToken", size, bytes: bytes);
            return;
        }

        // The redaction marker is the token's SHA-256 in hex (then '*' past 64 characters): show its two ends.
        Span<byte> marker = stackalloc byte[64];
        int shown = Math.Min(length, marker.Length);
        string hash = "";
        if (shown >= 8 && Read(store, bytes.Stream.Offset, marker[..shown]))
            hash = " · sha256 " + System.Text.Encoding.ASCII.GetString(marker[..4]) + "…" + System.Text.Encoding.ASCII.GetString(marker.Slice(shown - 2, 2));
        tree.Field("authenticationToken", size + " · redacted" + hash, PropertyTone.NexNet, bytes);
    }

    private static void Body(PropertyTreeModel tree, in FrameRecord frame, TransportOpStore store, in PayloadMap map)
    {
        if (frame.BodyLength <= 0) return;
        tree.Section("Body", frame.BodyLength.ToString("N0", Invariant) + " B");
        Span<byte> head = stackalloc byte[12];
        int shown = Math.Min(frame.BodyLength, head.Length);
        string preview = Read(store, frame.BodyOffset, head[..shown]) ? Convert.ToHexString(head[..shown]) + (frame.BodyLength > shown ? "…" : "") : "bytes no longer held";
        tree.Field("Bytes", preview, PropertyTone.Muted, map.Map(0, frame.BodyLength));
    }

    private static AssemblyInfo? Assembly(CaptureSession session, in FrameRecord frame, in AssembledMessage message, TraceFragment[] fragments)
    {
        if (fragments.Length < 2 && message.Kind != AssembledMessageKind.Large) return null;

        MessageAssembler assembler = session.Assembler;
        AssemblySegment[] segments = new AssemblySegment[fragments.Length];
        int number = 0;
        int current = -1;
        long firstTicks = long.MaxValue;
        long lastTicks = long.MinValue;
        for (int i = 0; i < fragments.Length; i++)
        {
            TraceFragment f = fragments[i];
            AssemblySegmentKind kind = f.Length > 0 ? AssemblySegmentKind.Chunk : i == 0 ? AssemblySegmentKind.Start : AssemblySegmentKind.Abort;
            if (kind == AssemblySegmentKind.Chunk) number++;
            int between = 0;
            if (i > 0 && fragments[i - 1].Direction == f.Direction) between = (int)Math.Max(0, f.Seq - fragments[i - 1].Seq - 1);
            bool isCurrent = f.Seq == frame.Seq && f.Direction == frame.Direction;
            if (isCurrent) current = i;
            segments[i] = new AssemblySegment(kind, kind == AssemblySegmentKind.Chunk ? number : 0, f.Length, isCurrent, between);
            if (assembler.TryGetFrame(f.Direction, f.Seq, out FrameRecord carrier))
            {
                firstTicks = Math.Min(firstTicks, carrier.StartTicks);
                lastTicks = Math.Max(lastTicks, carrier.EndTicks);
            }
        }

        string title;
        if (message.Kind == AssembledMessageKind.Large && message.Invocation >= 0)
        {
            ushort id = assembler.Invocations[message.Invocation].InvocationId;
            title = (message.LargeKind == LargeMessageKind.Result ? "Result #" : "Invocation #") + id.ToString(Invariant);
        }
        else if (message.Pipe >= 0)
        {
            title = "Pipe " + PipeName(assembler.Pipes[message.Pipe].PipeId) + " write";
        }
        else
        {
            title = "Message";
        }

        double ms = lastTicks > firstTicks ? (lastTicks - firstTicks) * 1000.0 / session.Header.StopwatchFrequency : 0;
        string summary = message.Length.ToString("N0", Invariant) + " B · " + number.ToString(Invariant) + (number == 1 ? " chunk" : " chunks") + " · "
                         + ms.ToString("N1", Invariant) + " ms";

        List<FrameRecord> interleaved = [];
        int others = assembler.GetInterleaved(message.Ref, interleaved);
        string note = "";
        if (current >= 0)
        {
            TraceFragment f = fragments[current];
            AssemblySegment s = segments[current];
            string what = s.Kind switch
            {
                AssemblySegmentKind.Start => "Start",
                AssemblySegmentKind.Abort => "Abort",
                _ => "Chunk " + s.Number.ToString(Invariant),
            };
            note = what + " arrived in " + Ops(f.Direction, f.FirstOpId, f.LastOpId);
        }

        if (others > 0) note += (note.Length > 0 ? " · " : "") + others.ToString(Invariant) + (others == 1 ? " frame" : " frames") + " interleaved";
        return new AssemblyInfo(title, summary, note, segments);
    }

    private static string Ops(TapDirection direction, long first, long last)
    {
        string one = direction == TapDirection.Inbound ? "read" : "write";
        string many = direction == TapDirection.Inbound ? "reads" : "writes";
        return (last - first) switch
        {
            <= 0 => one + " #" + first.ToString(Invariant),
            1 => many + " #" + first.ToString(Invariant) + " + #" + last.ToString(Invariant),
            _ => many + " #" + first.ToString(Invariant) + "–#" + last.ToString(Invariant),
        };
    }

    private static PayloadMap MapOf(CaptureSession session, MessageRef message, TraceFragment[] fragments)
    {
        if (message.Kind == MessageRefKind.Frame)
        {
            session.Assembler.TryGetFrame(message.Direction, message.Seq, out FrameRecord carrier);
            return PayloadMap.ForFrame(message.Direction, message.Seq, carrier.BodyOffset);
        }

        if (fragments.Length > 0) return PayloadMap.ForFragments(message, fragments);
        List<TraceFragment> list = [];
        session.Assembler.GetFragments(message, list);
        return PayloadMap.ForFragments(message, [.. list]);
    }

    /// <summary>"3·1": the client's then the server's local pipe id.</summary>
    public static string PipeName(ushort pipeId) => ((byte)pipeId).ToString(Invariant) + "·" + (pipeId >> 8).ToString(Invariant);

    private static string Word(TapDirection direction) => direction == TapDirection.Inbound ? "inbound" : "outbound";

    // ▼ arrives (inbound), ▲ leaves (outbound): the pane headers' IN / OUT legend.
    private static string Arrow(TapDirection direction) => direction == TapDirection.Inbound ? "▼" : "▲";

    private static string Lower(string word) => word.ToLowerInvariant();

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        Span<char> text = stackalloc char[(bytes.Length * 3) - 1];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) text[(i * 3) - 1] = ' ';
            bytes[i].TryFormat(text.Slice(i * 3, 2), out _, "X2", Invariant);
        }

        return new string(text);
    }

    // Reads stream bytes across ops; false when any of them is no longer held.
    private static bool Read(TransportOpStore store, long offset, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            if (!store.TryGetSegment(offset + done, out ReadOnlyMemory<byte> segment) || segment.IsEmpty) return false;
            int take = Math.Min(segment.Length, destination.Length - done);
            segment.Span[..take].CopyTo(destination[done..]);
            done += take;
        }

        return true;
    }
}
