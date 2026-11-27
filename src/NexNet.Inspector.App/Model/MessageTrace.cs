using System.Globalization;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Model;

/// <summary>What a <see cref="MessageTrace"/> follows.</summary>
public enum TraceKind : byte
{
    /// <summary>Nothing is traced: no selection, or a frame that is a whole message of its own.</summary>
    None = 0,

    /// <summary>An invocation: its request (MessageStart and chunks, or one frame), any cancellation and its result.</summary>
    Invocation = 1,

    /// <summary>An assembled message that belongs to no known invocation: a pipe data run, a channel item, an orphan.</summary>
    Message = 2,

    /// <summary>The handshake: both preambles and both greetings.</summary>
    Handshake = 3,

    /// <summary>Every frame of one pipe.</summary>
    Pipe = 4,
}

/// <summary>What one step of a trace is.</summary>
public enum TraceStepKind : byte
{
    /// <summary>A frame that carries a whole message (a single-frame request, a result, a pipe frame).</summary>
    Whole = 0,

    /// <summary>MessageStart: opens a large message, carries none of its bytes.</summary>
    Start = 1,

    /// <summary>A fragment that carries bytes of the message (a MessageChunk, a DuplexPipeWrite of a run).</summary>
    Chunk = 2,

    /// <summary>MessageAbort.</summary>
    Abort = 3,

    /// <summary>The caller's InvocationCancellation.</summary>
    Cancellation = 4,

    /// <summary>Frames of other messages that arrived between two fragments (one step for a run of them).</summary>
    Interleaved = 5,

    /// <summary>A preamble.</summary>
    Preamble = 6,

    /// <summary>A greeting.</summary>
    Greeting = 7,
}

/// <summary>
/// One step of a message trace: a frame that carried part of the traced message (or, for
/// <see cref="TraceStepKind.Interleaved"/>, the first of a run of other frames between two of them).
/// </summary>
/// <param name="Kind">What the step is.</param>
/// <param name="Frame">The frame (the first of an interleaved run).</param>
/// <param name="Ticks">When the frame started.</param>
/// <param name="Bytes">Its bytes on the wire (an interleaved run: all of its frames').</param>
/// <param name="MessageOffset">Offset of the fragment's first byte in the message; -1 when it carries none.</param>
/// <param name="StreamOffset">The frame's first byte in its direction's stream.</param>
/// <param name="Length">Bytes of the message the frame carried; 0 for MessageStart, MessageAbort and others.</param>
/// <param name="FirstOpId">The transport op that carried the frame's first byte.</param>
/// <param name="LastOpId">The transport op that carried its last byte.</param>
/// <param name="Flags">The frame's flags (T, N, X, R, …).</param>
/// <param name="Label">"MessageStart", "Chunk 2/5", "↳ pipe 3·1, Ping", ….</param>
/// <param name="Frames">Frames the step stands for: 1, or the length of an interleaved run.</param>
public readonly record struct TraceStep(
    TraceStepKind Kind,
    FrameKey Frame,
    long Ticks,
    int Bytes,
    long MessageOffset,
    long StreamOffset,
    int Length,
    long FirstOpId,
    long LastOpId,
    FrameFlags Flags,
    string Label,
    int Frames)
{
    /// <summary>True for a run of other messages' frames.</summary>
    public bool IsInterleaved => Kind == TraceStepKind.Interleaved;
}

/// <summary>
/// The one traced message every view follows (SYSTEM.md flow 2): the frames that carried it, in wire order, with the
/// frames of other messages interleaved between them, and the time range they span. Resolved once per selection from
/// the active capture session (<see cref="Resolve"/>) and shared through the sync bus: the timeline draws its band and
/// dims the rest, the ladder brackets its rows, the Events table tints them and the inspector lists the steps.
/// </summary>
/// <remarks>
/// Immutable. Membership is two sorted arrays of sequence numbers (one per direction), so the per-row question "is
/// this frame part of the trace" is a binary search that allocates nothing.
/// </remarks>
public sealed class MessageTrace
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly long[] _inbound;
    private readonly long[] _outbound;
    private readonly TraceStep[] _steps;

    private MessageTrace(TraceKind kind, MessageRef message, EntityKind entity, int entityIndex, string title, long start, long end,
        TraceStep[] steps, long[] inbound, long[] outbound)
    {
        Kind = kind;
        Message = message;
        Entity = entity;
        EntityIndex = entityIndex;
        Title = title;
        Start = start;
        End = end;
        _steps = steps;
        _inbound = inbound;
        _outbound = outbound;
    }

    /// <summary>Nothing traced.</summary>
    public static MessageTrace None { get; } = new(TraceKind.None, MessageRef.None, EntityKind.None, -1, "", 0, 0, [], [], []);

    /// <summary>What is traced.</summary>
    public TraceKind Kind { get; }

    /// <summary>True when something is traced.</summary>
    public bool IsActive => Kind != TraceKind.None;

    /// <summary>
    /// The message followed: an invocation's request (its result when the request was not captured), the assembled
    /// message, or the first handshake frame's message.
    /// </summary>
    public MessageRef Message { get; }

    /// <summary>The entity traced (an invocation, a pipe); <see cref="EntityKind.None"/> otherwise.</summary>
    public EntityKind Entity { get; }

    /// <summary>Index into the entity's list; -1 for none.</summary>
    public int EntityIndex { get; }

    /// <summary>"Invocation #812", "Pipe 3·1 write", "Handshake", ….</summary>
    public string Title { get; }

    /// <summary>The first member frame's first tick: where the trace band starts.</summary>
    public long Start { get; }

    /// <summary>The last member frame's last tick: where the trace band ends.</summary>
    public long End { get; }

    /// <summary>The steps in wire order: member frames and runs of interleaved frames.</summary>
    public ReadOnlySpan<TraceStep> Steps => _steps;

    /// <summary>The number of steps.</summary>
    public int StepCount => _steps.Length;

    /// <summary>Frames that carried part of the traced message.</summary>
    public int MemberCount => _inbound.Length + _outbound.Length;

    /// <summary>Frames of other messages between its fragments.</summary>
    public int InterleavedCount
    {
        get
        {
            int count = 0;
            foreach (ref readonly TraceStep step in Steps)
            {
                if (step.IsInterleaved) count += step.Frames;
            }

            return count;
        }
    }

    /// <summary>True when <paramref name="frame"/> carried part of the traced message.</summary>
    public bool Contains(FrameKey frame) =>
        Array.BinarySearch(frame.Direction == TapDirection.Inbound ? _inbound : _outbound, frame.Seq) >= 0;

    /// <summary>The step of a member frame (or the interleaved run starting at it); -1 for none.</summary>
    public int IndexOfStep(FrameKey frame)
    {
        for (int i = 0; i < _steps.Length; i++)
        {
            if (_steps[i].Frame == frame) return i;
        }

        return -1;
    }

    /// <summary>
    /// True when <paramref name="other"/> traces the same frames over the same span: a refresh that found nothing new
    /// keeps the trace (and the views keep what they drew).
    /// </summary>
    public bool SameAs(MessageTrace? other) =>
        other is not null && Kind == other.Kind && Message == other.Message && Entity == other.Entity && EntityIndex == other.EntityIndex
        && Start == other.Start && End == other.End && _steps.Length == other._steps.Length && MemberCount == other.MemberCount
        && Title == other.Title;

    /// <summary>
    /// What <paramref name="selection"/> traces in <paramref name="session"/>: a frame or message of an invocation traces
    /// the invocation; a preamble or greeting the handshake; a frame of a pipe run or channel item that assembled
    /// message; a pipe entity the pipe. A frame that is a whole message of its own traces nothing. Call under the
    /// session's gate.
    /// </summary>
    public static MessageTrace Resolve(CaptureSession session, in Selection selection)
    {
        ArgumentNullException.ThrowIfNull(session);
        MessageAssembler assembler = session.Assembler;
        switch (selection.Kind)
        {
            case SelectionKind.Entity when selection.Entity == EntityKind.Invocation && (uint)selection.EntityIndex < (uint)assembler.Invocations.Length:
                return Invocation(session, selection.EntityIndex);
            case SelectionKind.Entity when selection.Entity == EntityKind.Greeting:
                return Handshake(session);
            case SelectionKind.Entity when selection.Entity == EntityKind.Pipe && (uint)selection.EntityIndex < (uint)assembler.Pipes.Length:
                return Pipe(session, selection.EntityIndex);
            case SelectionKind.Message:
                return OfMessage(session, selection.Message);
            case SelectionKind.Frame:
                return OfFrame(session, selection.Frame);
            case SelectionKind.ByteRange when selection.Frame.Seq >= 0:
                return OfFrame(session, selection.Frame);
            default:
                return None;
        }
    }

    private static MessageTrace OfMessage(CaptureSession session, in MessageRef message)
    {
        MessageAssembler assembler = session.Assembler;
        switch (message.Kind)
        {
            case MessageRefKind.Assembled when (uint)message.Index < (uint)assembler.Messages.Length:
            {
                AssembledMessage assembled = assembler.Messages[message.Index];
                return assembled.Kind == AssembledMessageKind.Large && (uint)assembled.Invocation < (uint)assembler.Invocations.Length
                    ? Invocation(session, assembled.Invocation)
                    : Assembled(session, assembled.Ref);
            }
            case MessageRefKind.ChannelItem:
                return Assembled(session, message);
            case MessageRefKind.Frame:
                return OfFrame(session, new FrameKey(message.Direction, message.Seq));
            default:
                return None;
        }
    }

    private static MessageTrace OfFrame(CaptureSession session, FrameKey key)
    {
        MessageAssembler assembler = session.Assembler;
        if (!session.TryGetFrame(key, out FrameRecord frame)) return None;
        if (frame.Kind == FrameKind.Preamble) return Handshake(session);

        FrameLink link = assembler.GetLink(key.Direction, key.Seq);
        switch (link.Entity)
        {
            case EntityKind.Invocation when (uint)link.EntityIndex < (uint)assembler.Invocations.Length:
                return Invocation(session, link.EntityIndex);
            case EntityKind.Greeting:
                return Handshake(session);
        }

        return link.Message >= 0 ? OfMessage(session, assembler.Messages[link.Message].Ref) : None;
    }

    private static MessageTrace Invocation(CaptureSession session, int index)
    {
        MessageAssembler assembler = session.Assembler;
        InvocationRow invocation = assembler.Invocations[index];
        Builder builder = new(session);
        if (invocation.HasRequest) builder.AddMessage(invocation.Request, interleaved: true);
        if (invocation.CancellationSeq >= 0)
            builder.AddFrame(new FrameKey(invocation.Direction, invocation.CancellationSeq), TraceStepKind.Cancellation, null);
        if (!invocation.Result.IsNone) builder.AddMessage(invocation.Result, interleaved: true);
        if (builder.IsEmpty) return None;

        string title = invocation.State == InvocationState.OneWay
            ? "Invocation · one-way"
            : "Invocation #" + invocation.InvocationId.ToString(Invariant);
        if (invocation.RequestParsed && session.Decoder.TryGetMethod(invocation, out var method)) title += " · " + method.Name;
        MessageRef message = invocation.HasRequest ? invocation.Request : invocation.Result;
        return builder.Build(TraceKind.Invocation, message, EntityKind.Invocation, index, title);
    }

    private static MessageTrace Assembled(CaptureSession session, in MessageRef message)
    {
        MessageAssembler assembler = session.Assembler;
        Builder builder = new(session);
        builder.AddMessage(message, interleaved: true);
        if (builder.IsEmpty) return None;

        string title = "Message";
        if (message.Kind == MessageRefKind.Assembled)
        {
            AssembledMessage assembled = assembler.Messages[message.Index];
            if (assembled.Pipe >= 0) title = PipeText.Title(session, assembled.Pipe) + " write";
            else if (assembled.Kind == AssembledMessageKind.Large) title = "Large message · " + assembled.State.ToString().ToLowerInvariant();
        }
        else if (message.Kind == MessageRefKind.ChannelItem && (uint)message.Index < (uint)assembler.ChannelItems.Length)
        {
            ChannelItemRow item = assembler.ChannelItems[message.Index];
            title = PipeText.Title(session, item.Pipe) + " item #" + PipeText.Ordinal(assembler, item).ToString(Invariant);
        }

        return builder.Build(TraceKind.Message, message, EntityKind.None, -1, title);
    }

    private static MessageTrace Handshake(CaptureSession session)
    {
        MessageAssembler assembler = session.Assembler;
        Builder builder = new(session);
        foreach (TapDirection direction in (ReadOnlySpan<TapDirection>)[TapDirection.Inbound, TapDirection.Outbound])
        {
            ReadOnlySpan<FrameRecord> frames = assembler.GetFrames(direction);
            if (frames.Length > 0 && frames[0].Kind == FrameKind.Preamble)
                builder.AddFrame(new FrameKey(direction, frames[0].Seq), TraceStepKind.Preamble, null);
        }

        foreach (ref readonly GreetingRow greeting in assembler.Greetings)
            builder.AddFrame(new FrameKey(greeting.Direction, greeting.Seq), TraceStepKind.Greeting, null);
        if (builder.IsEmpty) return None;

        builder.SortByTime();
        MessageRef first = MessageRef.ForFrame(builder.First.Direction, builder.First.Seq);
        return builder.Build(TraceKind.Handshake, first, EntityKind.Greeting, -1, "Handshake");
    }

    private static MessageTrace Pipe(CaptureSession session, int index)
    {
        MessageAssembler assembler = session.Assembler;
        Builder builder = new(session);
        foreach (TapDirection direction in (ReadOnlySpan<TapDirection>)[TapDirection.Inbound, TapDirection.Outbound])
        {
            foreach (ref readonly FrameRecord frame in assembler.GetFrames(direction))
            {
                FrameLink link = assembler.GetLink(direction, frame.Seq);
                if (link.Entity == EntityKind.Pipe && link.EntityIndex == index)
                    builder.AddFrame(new FrameKey(direction, frame.Seq), TraceStepKind.Whole, null);
            }
        }

        if (builder.IsEmpty) return None;
        builder.SortByTime();
        return builder.Build(TraceKind.Pipe, MessageRef.None, EntityKind.Pipe, index, PipeText.Title(session, index));
    }

    /// <summary>"3·1": the client's then the server's local pipe id.</summary>
    internal static string PipeName(ushort pipeId) =>
        ((byte)pipeId).ToString(Invariant) + "·" + (pipeId >> 8).ToString(Invariant);

    // A frame's short name in an interleaved run: its pipe for pipe traffic, else its type.
    private static string ShortName(in FrameRecord frame) =>
        frame.Kind == FrameKind.Preamble ? "Preamble"
        : frame.Target == FrameTarget.Pipe ? "pipe " + PipeName(frame.TargetId)
        : frame.Type.ToString();

    private static string LabelOf(in FrameRecord frame, TraceStepKind kind, int part, int parts) => kind switch
    {
        TraceStepKind.Start => "MessageStart",
        TraceStepKind.Abort => "MessageAbort",
        TraceStepKind.Chunk when frame.Type == MessageType.MessageChunk => "Chunk " + part.ToString(Invariant) + "/" + parts.ToString(Invariant),
        TraceStepKind.Chunk => frame.Type + " " + part.ToString(Invariant) + "/" + parts.ToString(Invariant),
        TraceStepKind.Preamble => "Preamble",
        _ => frame.Type.ToString(),
    };

    // Collects member frames and interleaved frames, then orders and merges them into steps.
    private sealed class Builder(CaptureSession session)
    {
        private readonly List<TraceStep> _members = [];
        private readonly List<FrameRecord> _between = [];
        private readonly List<TraceFragment> _fragments = [];

        public bool IsEmpty => _members.Count == 0;

        public FrameKey First => _members[0].Frame;

        public void AddFrame(FrameKey key, TraceStepKind kind, string? label)
        {
            if (!session.TryGetFrame(key, out FrameRecord frame)) return;
            _members.Add(Step(frame, kind, -1, 0, label ?? LabelOf(frame, kind, 0, 0)));
        }

        public void AddMessage(in MessageRef message, bool interleaved)
        {
            MessageAssembler assembler = session.Assembler;
            if (message.Kind == MessageRefKind.Frame)
            {
                AddFrame(new FrameKey(message.Direction, message.Seq), TraceStepKind.Whole, null);
                return;
            }

            _fragments.Clear();
            assembler.GetFragments(message, _fragments);
            int parts = 0;
            foreach (TraceFragment f in _fragments)
            {
                if (f.Length > 0) parts++;
            }

            int part = 0;
            for (int i = 0; i < _fragments.Count; i++)
            {
                TraceFragment f = _fragments[i];
                if (!assembler.TryGetFrame(f.Direction, f.Seq, out FrameRecord frame)) continue;
                TraceStepKind kind = f.Length > 0 ? TraceStepKind.Chunk : i == 0 ? TraceStepKind.Start : TraceStepKind.Abort;
                if (kind == TraceStepKind.Chunk) part++;
                _members.Add(Step(frame, kind, f.MessageOffset, f.Length, LabelOf(frame, kind, part, parts)) with
                {
                    StreamOffset = f.StreamOffset,
                });
            }

            if (interleaved) assembler.GetInterleaved(message, _between);
        }

        public void SortByTime() => _members.Sort(static (a, b) => Order(a.Ticks, a.Frame, b.Ticks, b.Frame));

        public MessageTrace Build(TraceKind kind, MessageRef message, EntityKind entity, int entityIndex, string title)
        {
            List<TraceStep> all = new(_members.Count + _between.Count);
            all.AddRange(_members);
            foreach (FrameRecord frame in _between)
            {
                FrameKey key = new(frame.Direction, frame.Seq);
                if (!Has(key)) all.Add(Step(frame, TraceStepKind.Interleaved, -1, 0, ShortName(frame)));
            }

            // Wire order: by start tick, then by sequence within a direction (a frame's fragments keep their order).
            all.Sort(static (a, b) => Order(a.Ticks, a.Frame, b.Ticks, b.Frame));
            List<TraceStep> steps = new(all.Count);
            List<long> inbound = [];
            List<long> outbound = [];
            long start = long.MaxValue;
            long end = long.MinValue;
            for (int i = 0; i < all.Count; i++)
            {
                TraceStep step = all[i];
                if (!step.IsInterleaved)
                {
                    (step.Frame.Direction == TapDirection.Inbound ? inbound : outbound).Add(step.Frame.Seq);
                    session.TryGetFrame(step.Frame, out FrameRecord frame);
                    start = Math.Min(start, frame.StartTicks);
                    end = Math.Max(end, Math.Max(frame.StartTicks, frame.EndTicks));
                    steps.Add(step);
                    continue;
                }

                // A run of interleaved frames is one step: "↳ pipe 3·1, Ping".
                int j = i;
                int bytes = 0;
                List<string> names = [];
                while (j < all.Count && all[j].IsInterleaved)
                {
                    bytes += all[j].Bytes;
                    if (!names.Contains(all[j].Label)) names.Add(all[j].Label);
                    j++;
                }

                steps.Add(step with { Bytes = bytes, Frames = j - i, Label = "↳ " + string.Join(", ", names) });
                i = j - 1;
            }

            inbound.Sort();
            outbound.Sort();
            return new MessageTrace(kind, message, entity, entityIndex, title, start, end, [.. steps], DistinctArray(inbound), DistinctArray(outbound));
        }

        private bool Has(FrameKey key)
        {
            foreach (TraceStep step in _members)
            {
                if (step.Frame == key) return true;
            }

            return false;
        }

        private static TraceStep Step(in FrameRecord frame, TraceStepKind kind, long messageOffset, int length, string label) =>
            new(kind, new FrameKey(frame.Direction, frame.Seq), frame.StartTicks, frame.Length, messageOffset, frame.Offset, length,
                frame.FirstOpId, frame.LastOpId, frame.Flags, label, 1);

        // A direction's frames start in sequence order, so time then sequence is wire order within each direction too.
        private static int Order(long ticksA, FrameKey a, long ticksB, FrameKey b)
        {
            int c = ticksA.CompareTo(ticksB);
            if (c != 0) return c;
            c = a.Direction.CompareTo(b.Direction);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }

        private static long[] DistinctArray(List<long> sorted)
        {
            int n = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (n == 0 || sorted[n - 1] != sorted[i]) sorted[n++] = sorted[i];
            }

            return [.. sorted.GetRange(0, n)];
        }
    }
}
