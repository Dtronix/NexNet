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

        bool handshake = frame.Kind == FrameKind.Preamble || link.Entity == EntityKind.Greeting;
        if (handshake)
        {
            HandshakeFrame(tree, session, frame, link, store, frameMap);
            tree.Complete();
            return new InspectorContent(key, assembler.GetMessage(frame.Direction, frame.Seq), tree, "#" + frame.Seq.ToString("N0", Invariant), true,
                HandshakeAssembly(session, key), (frame.Flags & FrameFlags.Incomplete) == 0 && HandshakeComplete(assembler));
        }

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
            default:
                Body(tree, frame, store, frameMap);
                break;
        }

        tree.Complete();
        AssemblyInfo? assembly = link.Message >= 0 ? Assembly(session, frame, assembler.Messages[link.Message], fragments) : null;
        bool final = (frame.Flags & FrameFlags.Incomplete) == 0
                     && (link.Message < 0 || assembler.Messages[link.Message].State != AssemblyState.Assembling)
                     && link.Entity switch
                     {
                         EntityKind.Invocation => assembler.Invocations[link.EntityIndex].State != InvocationState.Pending,
                         EntityKind.Pipe => assembler.Pipes[link.EntityIndex].Lifecycle is PipeLifecycle.Closed or PipeLifecycle.Refused,
                         _ => true,
                     };
        return new InspectorContent(key, message, tree, "#" + frame.Seq.ToString("N0", Invariant), false, assembly, final);
    }

    /// <summary>
    /// The Handshake tab (inspect-session-start): every handshake frame in wire order, each a section of its decoded
    /// fields — the preambles' magic and version, the ClientGreeting's versions, nexus hashes, the redacted token (its
    /// marker's SHA-256 prefix) and credits, the ServerGreeting's session id and limits. Call under the session's gate.
    /// </summary>
    /// <returns>The tree; null when the session holds no handshake frame (attached mid-session).</returns>
    public static PropertyTreeModel? BuildHandshake(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        MessageTrace trace = MessageTrace.Resolve(session, Selection.OfEntity(EntityKind.Greeting, 0));
        if (trace.Kind != TraceKind.Handshake) return null;

        MessageAssembler assembler = session.Assembler;
        PropertyTreeModel tree = new();
        foreach (ref readonly TraceStep step in trace.Steps)
        {
            if (!session.TryGetFrame(step.Frame, out FrameRecord frame)) continue;
            TransportOpStore store = session.GetStore(frame.Direction);
            PayloadMap map = PayloadMap.ForFrame(frame.Direction, frame.Seq, frame.BodyOffset);
            string where = "#" + frame.Seq.ToString("N0", Invariant) + " · " + Ms(session, frame.StartTicks) + " ms";
            if (frame.Kind == FrameKind.Preamble)
            {
                Preamble(tree, frame, store, map, "Preamble " + Arrow(frame.Direction), where);
                continue;
            }

            FrameLink link = assembler.GetLink(frame.Direction, frame.Seq);
            if (link.Entity != EntityKind.Greeting) continue;
            tree.Section(frame.Type + " " + Arrow(frame.Direction), where);
            GreetingFields(tree, assembler.Greetings[link.EntityIndex], store, map);
        }

        tree.Complete();
        return tree;
    }

    // A preamble or greeting on the Decoded tab: the preamble that shared its read, the frame's own section (its type
    // and length bytes, then the greeting's fields), and the read that carried it with the other side's reply.
    private static void HandshakeFrame(PropertyTreeModel tree, CaptureSession session, in FrameRecord frame, in FrameLink link,
        TransportOpStore store, in PayloadMap map)
    {
        MessageAssembler assembler = session.Assembler;
        string where = Word(frame.Direction) + " · " + Ms(session, frame.StartTicks) + " ms";
        if (frame.Kind == FrameKind.Preamble)
        {
            Preamble(tree, frame, store, map, "Preamble", where);
        }
        else
        {
            ReadOnlySpan<FrameRecord> frames = assembler.GetFrames(frame.Direction);
            if (frames.Length > 0 && frames[0].Kind == FrameKind.Preamble && frames[0].Seq != frame.Seq && frames[0].LastOpId == frame.FirstOpId)
            {
                FrameRecord preamble = frames[0];
                Preamble(tree, preamble, store, PayloadMap.ForFrame(preamble.Direction, preamble.Seq, preamble.BodyOffset), "Preamble",
                    "#" + preamble.Seq.ToString("N0", Invariant) + " · same " + (frame.Direction == TapDirection.Inbound ? "read" : "write"));
            }

            tree.Section(frame.Type.ToString(), where);
            Span<byte> wire = stackalloc byte[2];
            long toBody = frame.Offset - frame.BodyOffset;
            tree.Field("Type", "0x" + ((byte)frame.Type).ToString("X2", Invariant) + " · " + ((byte)frame.Type).ToString(Invariant),
                bytes: map.Map(toBody, 1));
            if (frame.HeaderLength >= 3)
            {
                string le = Read(store, frame.Offset + 1, wire) ? " · LE " + Hex(wire) : "";
                tree.Field("Body length", frame.BodyLength.ToString("N0", Invariant) + le, bytes: map.Map(toBody + 1, 2));
            }

            if (link.Entity == EntityKind.Greeting) GreetingFields(tree, assembler.Greetings[link.EntityIndex], store, map);
        }

        HandshakeTransport(tree, session, frame, store, map);
    }

    // The preamble's eight bytes: the magic "NnP\x14", three reserved zeros and the protocol version.
    private static void Preamble(PropertyTreeModel tree, in FrameRecord frame, TransportOpStore store, in PayloadMap map, string title, string summary)
    {
        tree.Section(title, summary);
        long toBody = frame.Offset - frame.BodyOffset;
        Span<byte> bytes = stackalloc byte[FrameLayout.PreambleLength];
        if (frame.Length < FrameLayout.PreambleLength || !Read(store, frame.Offset, bytes))
        {
            tree.Field("Bytes", "no longer held", PropertyTone.Muted, map.Map(toBody, frame.Length));
            return;
        }

        bool magic = bytes[..4].SequenceEqual(FrameLayout.PreambleMagic);
        bool reserved = bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0;
        tree.Field("Magic", Hex(bytes[..4]) + (magic ? " ✓" : " ✗"), magic ? PropertyTone.Normal : PropertyTone.NexNet, map.Map(toBody, 4));
        tree.Field("Reserved · version", Hex(bytes[4..7]) + " · " + bytes[7].ToString(Invariant) + (reserved ? " ✓" : " ✗"),
            reserved ? PropertyTone.Normal : PropertyTone.NexNet, map.Map(toBody + 4, 4));
    }

    // "read #1 · 129 B · 2 frames": the op that carried the frame, its other frames, and the other side's answer.
    private static void HandshakeTransport(PropertyTreeModel tree, CaptureSession session, in FrameRecord frame, TransportOpStore store, in PayloadMap map)
    {
        MessageAssembler assembler = session.Assembler;
        string unit = frame.Direction == TapDirection.Inbound ? "read" : "write";
        string summary;
        if (frame.FirstOpId == frame.LastOpId && store.TryFindOp(frame.Offset, out int index))
        {
            StreamOp op = store.Ops[index];
            int carried = FramesIn(assembler.GetFrames(frame.Direction), op.Offset, op.End);
            summary = unit + " #" + op.OpId.ToString(Invariant) + " · " + op.Length.ToString("N0", Invariant) + " B · " + carried.ToString(Invariant)
                      + (carried == 1 ? " frame" : " frames");
        }
        else
        {
            long ops = frame.LastOpId - frame.FirstOpId + 1;
            summary = "split across " + ops.ToString(Invariant) + " " + unit + "s";
        }

        tree.Section("Transport", summary);
        long end = frame.Offset + frame.Length;
        if (store.TryFindOp(frame.Offset, out int first))
        {
            ReadOnlySpan<StreamOp> all = store.Ops;
            for (int i = first; i < all.Length && all[i].Offset < end; i++)
            {
                StreamOp op = all[i];
                long from = Math.Max(op.Offset, frame.Offset);
                long to = Math.Min(op.End, end);
                if (to <= from) continue;
                tree.Field("#" + op.OpId.ToString(Invariant), from.ToString("X6", Invariant) + " – " + (to - 1).ToString("X6", Invariant),
                    PropertyTone.Transport, map.Map(from - frame.BodyOffset, (int)(to - from)));
            }
        }

        // The other side's greeting: how long the answer took (or how long before it the request came).
        FrameLink link = assembler.GetLink(frame.Direction, frame.Seq);
        if (link.Entity != EntityKind.Greeting) return;
        GreetingKind own = assembler.Greetings[link.EntityIndex].Kind;
        foreach (ref readonly GreetingRow other in assembler.Greetings)
        {
            if (other.Direction == frame.Direction || (own == GreetingKind.Server) == (other.Kind == GreetingKind.Server)) continue;
            if (!assembler.TryGetFrame(other.Direction, other.Seq, out FrameRecord reply)) continue;
            double ms = (reply.StartTicks - frame.StartTicks) * 1000.0 / session.Header.StopwatchFrequency;
            string name = own == GreetingKind.Server ? "Client greeting" : "Server reply";
            tree.Field(name, reply.Type + " " + (ms >= 0 ? "+" : "") + ms.ToString("N2", Invariant) + " ms " + Arrow(reply.Direction));
            break;
        }
    }

    // How many frames of a direction overlap a stream range (the frames one op carried).
    private static int FramesIn(ReadOnlySpan<FrameRecord> frames, long from, long to)
    {
        int lo = 0, hi = frames.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (frames[mid].Offset + frames[mid].Length <= from) lo = mid + 1;
            else hi = mid;
        }

        int count = 0;
        for (int i = lo; i < frames.Length && frames[i].Offset < to; i++) count++;
        return count;
    }

    private static bool HandshakeComplete(MessageAssembler assembler)
    {
        foreach (ref readonly GreetingRow greeting in assembler.Greetings)
        {
            if (greeting.Kind == GreetingKind.Server) return true;
        }

        return false;
    }

    /// <summary>
    /// The handshake's assembly bar: Pre ▼ / Greet ▼ / Pre ▲ / Greet ▲ in wire order with the selected frame current,
    /// "4 frames · 3.1 ms · auth ok", and how the reads carried them.
    /// </summary>
    private static AssemblyInfo? HandshakeAssembly(CaptureSession session, FrameKey current)
    {
        MessageTrace trace = MessageTrace.Resolve(session, Selection.OfFrame(current));
        if (trace.Kind != TraceKind.Handshake) return null;

        MessageAssembler assembler = session.Assembler;
        ReadOnlySpan<TraceStep> steps = trace.Steps;
        AssemblySegment[] segments = new AssemblySegment[steps.Length];
        bool serverReplied = false;
        bool redacted = false;
        string note = "";
        for (int i = 0; i < steps.Length; i++)
        {
            TraceStep step = steps[i];
            bool preamble = step.Kind == TraceStepKind.Preamble;
            // The bar gives a greeting twice a preamble's room, as the package draws it, whatever their bytes.
            segments[i] = new AssemblySegment(preamble ? AssemblySegmentKind.Preamble : AssemblySegmentKind.Greeting, 0, preamble ? 1 : 2,
                step.Frame == current, 0, step.Frame.Direction);
            if (preamble || !assembler.TryGetFrame(step.Frame.Direction, step.Frame.Seq, out FrameRecord frame)) continue;
            FrameLink link = assembler.GetLink(frame.Direction, frame.Seq);
            if (link.Entity != EntityKind.Greeting) continue;
            GreetingRow greeting = assembler.Greetings[link.EntityIndex];
            serverReplied |= greeting.Kind == GreetingKind.Server;
            if (greeting.Kind != GreetingKind.Server)
            {
                redacted |= (frame.Flags & FrameFlags.Redacted) != 0 || Overlaps(session.GetStore(frame.Direction).Redactions, frame.Offset, frame.Offset + frame.Length);
                // "Preamble and ClientGreeting arrived in one read (#1)": the client's opening shares its first op.
                if (i > 0 && steps[i - 1].Kind == TraceStepKind.Preamble && steps[i - 1].Frame.Direction == frame.Direction
                    && steps[i - 1].LastOpId == frame.FirstOpId)
                {
                    string unit = frame.Direction == TapDirection.Inbound ? "read" : "write";
                    note = "Preamble and " + frame.Type + " arrived in one " + unit + " (#" + frame.FirstOpId.ToString(Invariant) + ")";
                }
            }
        }

        if (redacted) note += (note.Length > 0 ? " · " : "") + "token redacted";
        double ms = (trace.End - trace.Start) * 1000.0 / session.Header.StopwatchFrequency;
        string summary = steps.Length.ToString(Invariant) + (steps.Length == 1 ? " frame" : " frames") + " · " + ms.ToString("N1", Invariant) + " ms"
                         + (serverReplied ? " · auth ok" : " · awaiting server");
        return new AssemblyInfo("Handshake", summary, note, segments);
    }

    private static bool Overlaps(ReadOnlySpan<StreamRange> ranges, long from, long to)
    {
        foreach (StreamRange r in ranges)
        {
            if (r.Offset < to && from < r.End) return true;
        }

        return false;
    }

    private static string Ms(CaptureSession session, long ticks) =>
        ((ticks - session.StartTicks) * 1000.0 / session.Header.StopwatchFrequency).ToString("N3", Invariant);

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

    // A greeting's fields under their wire names (spec §4.1, §4.2), integers in decimal as the schema shows them.
    private static void GreetingFields(PropertyTreeModel tree, in GreetingRow greeting, TransportOpStore store, in PayloadMap map)
    {
        if (greeting.Kind == GreetingKind.Server)
        {
            tree.Field("clientId", "0x" + greeting.ClientId.ToString("X", Invariant) + " · session id");
            tree.Field("version", greeting.ClientNexusHash.ToString("N0", Invariant) + " · client nexus hash");
            if (greeting.IdleTimeoutMs > 0) tree.Field("idleTimeout", greeting.IdleTimeoutMs.ToString("N0", Invariant) + " ms");
        }
        else
        {
            tree.Field("version", greeting.Version is { } version ? "\"" + version + "\" · str" : "nil", greeting.Version is null ? PropertyTone.Muted : PropertyTone.Normal);
            tree.Field("serverNexusHash", greeting.ServerNexusHash.ToString("N0", Invariant));
            tree.Field("clientNexusHash", greeting.ClientNexusHash.ToString("N0", Invariant));
            Token(tree, greeting.Token, store, map);
        }

        tree.Field("maxMessageSize", greeting.MaxMessageSize.ToString("N0", Invariant));
        tree.Field("invocationCredits", greeting.InvocationCredits.ToString("N0", Invariant));
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
