using NexNet.Inspector.App.Model;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Schema;
using NexNet.Inspector.Core.Session;
using NexNet.Messages;

namespace NexNet.Inspector.App.Controls.Ladder;

/// <summary>
/// Projects a <see cref="CaptureSession"/> onto a <see cref="LadderModel"/>: a row per frame of both directions, in the
/// order the parsers completed them, with the target, note and chunk number assembly worked out.
/// </summary>
/// <remarks>
/// Incremental: frames are appended from where the last update stopped. Assembly can still add flags (N, X) to frames of
/// a message that is assembling, so the rows of such messages are read again on each update until the message ends.
/// Call it on the UI thread; it reads the session under its gate.
/// </remarks>
public sealed class LadderProjection
{
    private readonly LadderModel _model;
    private readonly Dictionary<int, int> _chunks = [];
    private readonly List<int> _open = [];
    private CaptureSession? _session;
    private int _next;

    /// <param name="model">The model to fill.</param>
    public LadderProjection(LadderModel model)
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
            (_model.LeftEndpoint, _model.RightEndpoint) = Endpoints(session.Open);
        }

        lock (session.Gate)
        {
            _model.Frequency = session.Header.StopwatchFrequency;
            _model.Origin = session.StartTicks;
            RefreshOpen(session);
            ReadOnlySpan<FrameKey> order = session.FrameOrder;
            for (int i = _next; i < order.Length; i++)
            {
                if (!session.TryGetFrame(order[i], out FrameRecord frame)) continue;
                LadderRow row = Project(session, frame, Part(session, frame));
                int index = _model.Add(row);
                if (IsAssembling(session, row.Message)) _open.Add(index);
            }

            _next = order.Length;
        }

        _model.NotifyChanged();
    }

    /// <summary>Empties the model (a detach, another document).</summary>
    public void Reset()
    {
        _session = null;
        _next = 0;
        _chunks.Clear();
        _open.Clear();
        _model.Clear();
        _model.NotifyChanged();
    }

    /// <summary>The two column titles: the remote peer on the left, the tapped endpoint on the right.</summary>
    public static (string Left, string Right) Endpoints(in NntlSessionOpen open)
    {
        string local = open.Role == NntlSessionRole.Server ? "Server" : "Client";
        string remote = open.Role == NntlSessionRole.Server ? "Client" : "Server";
        return (string.IsNullOrEmpty(open.RemoteEndpoint) ? remote : remote + " · " + open.RemoteEndpoint, local);
    }

    private LadderRow Project(CaptureSession session, in FrameRecord frame, int part)
    {
        MessageAssembler assembler = session.Assembler;
        FrameLink link = assembler.GetLink(frame.Direction, frame.Seq);
        LadderTarget target = LadderTarget.None;
        ushort targetId = 0;
        LadderNote note = LadderNote.None;
        int name = -1;
        FrameItems items = FrameItems.None;

        if (link.Entity == EntityKind.Invocation && (uint)link.EntityIndex < (uint)assembler.Invocations.Length)
        {
            ref readonly InvocationRow invocation = ref assembler.Invocations[link.EntityIndex];
            target = LadderTarget.Invocation;
            targetId = invocation.InvocationId;
            if (frame.Type == MessageType.Invocation && invocation.RequestParsed && session.Decoder.TryGetMethod(invocation, out SchemaMethod? method))
                name = _model.AddLabel(method.Name);
            if (frame.Type == MessageType.InvocationResult)
                note = Result(invocation.ResultState);
        }
        else if (frame.Target == FrameTarget.Invocation)
        {
            target = LadderTarget.Invocation;
            targetId = frame.TargetId;
        }
        else if (frame.Target == FrameTarget.Pipe)
        {
            bool linked = link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length;
            target = linked ? TargetOf(assembler.Pipes[link.EntityIndex].Content) : LadderTarget.Pipe;
            targetId = frame.TargetId;
            if (linked && session.Decoder.GetPipeLabel(link.EntityIndex) is { } label)
                name = _model.AddLabel(label);
            if (linked && frame.Type == MessageType.DuplexPipeWrite)
                items = PipeText.Find(assembler, link.EntityIndex, frame.Direction, frame.Seq);
        }
        else if (link.Entity == EntityKind.Pipe && (uint)link.EntityIndex < (uint)assembler.Pipes.Length)
        {
            // DuplexPipeUpdateState names its pipe in the body, not a post-header: assembly linked it.
            target = TargetOf(assembler.Pipes[link.EntityIndex].Content);
            targetId = assembler.Pipes[link.EntityIndex].PipeId;
        }

        if (frame.Type == MessageType.MessageStart && (uint)link.Message < (uint)assembler.Messages.Length)
            note = assembler.Messages[link.Message].LargeKind == LargeMessageKind.Result ? LadderNote.Result : LadderNote.Argument;
        if ((frame.Flags & FrameFlags.Incomplete) != 0)
            note = LadderNote.Partial;
        if ((uint)link.Message < (uint)assembler.Messages.Length)
            _model.SetMessage(link.Message, assembler.Messages[link.Message].Ref);

        // Results and large messages already say their state or kind through the note.
        FrameDetail detail = assembler.GetDetail(frame.Direction, frame.Seq);
        if (detail.Kind is FrameDetailKind.Result or FrameDetailKind.MessageStart) detail = FrameDetail.None;

        return new LadderRow(new FrameKey(frame.Direction, frame.Seq), frame.StartTicks, frame.Kind, frame.Type, frame.Flags,
            Bytes(frame), target, targetId, note, name, frame.FirstOpId, frame.LastOpId, link.Entity, link.EntityIndex, link.Message, part, detail,
            items.Count, items.Partial);
    }

    private static LadderTarget TargetOf(PipeContent content) => content switch
    {
        PipeContent.Channel => LadderTarget.Channel,
        PipeContent.Collection => LadderTarget.Collection,
        _ => LadderTarget.Pipe,
    };

    // Chunks are numbered as they are projected, which is stream order within the message's direction.
    private int Part(CaptureSession session, in FrameRecord frame)
    {
        if (frame.Type != MessageType.MessageChunk) return 0;
        int message = session.Assembler.GetLink(frame.Direction, frame.Seq).Message;
        if (message < 0) return 0;
        int part = _chunks.GetValueOrDefault(message) + 1;
        _chunks[message] = part;
        return part;
    }

    private void RefreshOpen(CaptureSession session)
    {
        int kept = 0;
        for (int i = 0; i < _open.Count; i++)
        {
            int index = _open[i];
            ref readonly LadderRow row = ref _model.RowAt(index);
            if (session.TryGetFrame(row.Frame, out FrameRecord frame))
            {
                LadderRow fresh = Project(session, frame, row.Part);
                if (fresh != row) _model.Replace(index, fresh);
            }

            if (IsAssembling(session, _model.RowAt(index).Message)) _open[kept++] = index;
        }

        _open.RemoveRange(kept, _open.Count - kept);
    }

    private static bool IsAssembling(CaptureSession session, int message) =>
        (uint)message < (uint)session.Assembler.Messages.Length && session.Assembler.Messages[message].State == AssemblyState.Assembling;

    private static int Bytes(in FrameRecord frame)
    {
        if (frame.Kind != FrameKind.Frame) return frame.Length;
        if (frame.HeaderLength < 3) return -1;
        return frame.BodyLength > 0 ? frame.BodyLength : Math.Max(0, frame.Length - frame.HeaderLength - frame.PostHeaderLength);
    }

    private static LadderNote Result(InvocationResultState state) => state switch
    {
        InvocationResultState.CompletedResult => LadderNote.Completed,
        InvocationResultState.Exception => LadderNote.Exception,
        InvocationResultState.Unauthorized => LadderNote.Unauthorized,
        InvocationResultState.Canceled => LadderNote.Canceled,
        _ => LadderNote.None,
    };
}
