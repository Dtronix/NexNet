using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Core.Compare;

/// <summary>One entity of one document, before the union.</summary>
internal readonly record struct SideEntity(EntityGroupKey Group, EntitySide Side);

/// <summary>
/// Lists the entities of one document in one pass over its frames (start, end, frames and bytes per entity, from each
/// frame's assembly link) plus one pass over its payloads for the content digests. Runs under the session's
/// <see cref="CaptureSession.Gate"/>.
/// </summary>
internal static class EntityCollector
{
    public static List<SideEntity> Collect(CaptureSession session)
    {
        var assembler = session.Assembler;
        var role = session.Role;
        var invocations = assembler.Invocations;
        var pipes = assembler.Pipes;
        var greetings = assembler.Greetings;
        var collections = assembler.Collections;

        var invocationStats = new FrameStats[invocations.Length];
        var pipeStats = new FrameStats[pipes.Length];
        var greetingStats = new FrameStats[greetings.Length];
        Span<FrameStats> preambleStats = stackalloc FrameStats[2];
        Span<long> preambleSeq = [-1, -1];
        var control = new Dictionary<EntityGroupKey, FrameStats>();

        for (var d = 0; d < 2; d++)
        {
            var direction = (TapDirection)d;
            var flow = FlowOf(role, direction);
            foreach (ref readonly var frame in assembler.GetFrames(direction))
            {
                if (frame.Kind == FrameKind.Preamble)
                {
                    preambleStats[d].Add(frame);
                    preambleSeq[d] = frame.Seq;
                    continue;
                }

                var link = assembler.GetLink(direction, frame.Seq);
                switch (link.Entity)
                {
                    case EntityKind.Invocation when (uint)link.EntityIndex < (uint)invocationStats.Length:
                        invocationStats[link.EntityIndex].Add(frame);
                        break;
                    case EntityKind.Pipe when (uint)link.EntityIndex < (uint)pipeStats.Length:
                        pipeStats[link.EntityIndex].Add(frame);
                        break;
                    case EntityKind.Greeting when (uint)link.EntityIndex < (uint)greetingStats.Length:
                        greetingStats[link.EntityIndex].Add(frame);
                        break;
                    default:
                        var key = new EntityGroupKey(CompareEntityKind.Control, flow, (ushort)frame.Type);
                        control.TryGetValue(key, out var stats);
                        stats.Add(frame);
                        control[key] = stats;
                        break;
                }
            }
        }

        var items = new int[pipes.Length];
        foreach (ref readonly var item in assembler.ChannelItems)
        {
            if ((uint)item.Pipe < (uint)items.Length)
                items[item.Pipe]++;
        }

        var result = new List<SideEntity>(invocations.Length + pipes.Length + greetings.Length + control.Count + 2);
        for (var i = 0; i < invocations.Length; i++)
        {
            ref readonly var row = ref invocations[i];
            var hasher = new ContentHasher();
            AppendPayload(assembler, row.Arguments, ref hasher);
            hasher.Boundary(1);
            AppendPayload(assembler, row.Value, ref hasher);
            var side = invocationStats[i].ToSide(i, OutcomeOf(row.State), 0, hasher.Finish());
            result.Add(new SideEntity(new EntityGroupKey(CompareEntityKind.Invocation, FlowOf(role, row.Direction), MethodOf(row)), side));
        }

        for (var p = 0; p < pipes.Length; p++)
        {
            ref readonly var row = ref pipes[p];
            if (row.Collection >= 0)
                continue;
            var (flow, id) = OpenerOf(assembler, role, row);
            var side = pipeStats[p].ToSide(p, OutcomeOf(row.Lifecycle), items[p], PipeDigest(assembler, role, p));
            result.Add(new SideEntity(new EntityGroupKey(CompareEntityKind.Pipe, flow, id), side));
        }

        for (var c = 0; c < collections.Length; c++)
        {
            ref readonly var row = ref collections[c];
            if ((uint)row.Pipe >= (uint)pipes.Length)
                continue;
            ref readonly var pipe = ref pipes[row.Pipe];
            var (flow, _) = OpenerOf(assembler, role, pipe);
            var side = pipeStats[row.Pipe].ToSide(c, OutcomeOf(pipe.Lifecycle), row.MessageCount, PipeDigest(assembler, role, row.Pipe));
            result.Add(new SideEntity(new EntityGroupKey(CompareEntityKind.Collection, flow, row.CollectionId), side));
        }

        for (var d = 0; d < 2; d++)
        {
            if (preambleSeq[d] < 0)
                continue;
            var side = preambleStats[d].ToSide((int)preambleSeq[d], EntityOutcome.Seen, 0, 0);
            result.Add(new SideEntity(new EntityGroupKey(CompareEntityKind.Handshake, FlowOf(role, (TapDirection)d), (ushort)HandshakeItem.Preamble), side));
        }

        for (var g = 0; g < greetings.Length; g++)
        {
            ref readonly var row = ref greetings[g];
            var hasher = new ContentHasher();
            if (assembler.TryGetFrame(row.Direction, row.Seq, out var frame))
                AppendPayload(assembler, new PayloadRange(MessageRef.ForFrame(row.Direction, row.Seq), 0, frame.BodyLength), ref hasher);
            var item = row.Kind switch
            {
                GreetingKind.Client => HandshakeItem.ClientGreeting,
                GreetingKind.ClientReconnection => HandshakeItem.ClientReconnection,
                _ => HandshakeItem.ServerGreeting,
            };
            var side = greetingStats[g].ToSide(g, EntityOutcome.Seen, 0, hasher.Finish());
            result.Add(new SideEntity(new EntityGroupKey(CompareEntityKind.Handshake, FlowOf(role, row.Direction), (ushort)item), side));
        }

        foreach (var (key, stats) in control)
            result.Add(new SideEntity(key, stats.ToSide(-1, EntityOutcome.Seen, 0, 0)));
        return result;
    }

    /// <summary>The wire flow of a tap direction: the tapped endpoint reads what its peer sends.</summary>
    public static WireFlow FlowOf(NntlSessionRole role, TapDirection direction)
        => (role == NntlSessionRole.Server) == (direction == TapDirection.Inbound) ? WireFlow.ClientToServer : WireFlow.ServerToClient;

    /// <summary>The tap direction that carries a flow in a document of this role.</summary>
    public static TapDirection DirectionOf(NntlSessionRole role, WireFlow flow)
        => (role == NntlSessionRole.Server) == (flow == WireFlow.ClientToServer) ? TapDirection.Inbound : TapDirection.Outbound;

    /// <summary>The flow and method of the invocation that opened a pipe.</summary>
    public static (WireFlow Flow, ushort Id) OpenerOf(MessageAssembler assembler, NntlSessionRole role, in PipeRow pipe)
    {
        if ((uint)pipe.OpenerInvocation < (uint)assembler.Invocations.Length)
        {
            ref readonly var opener = ref assembler.Invocations[pipe.OpenerInvocation];
            return (FlowOf(role, opener.Direction), MethodOf(opener));
        }

        return (pipe.OpenerDirection is { } direction ? FlowOf(role, direction) : WireFlow.ClientToServer, EntityKey.UnknownId);
    }

    public static ushort MethodOf(in InvocationRow row) => row.RequestParsed ? row.MethodId : EntityKey.UnknownId;

    /// <summary>Appends a payload's bytes; nothing for an absent or unreadable one.</summary>
    public static void AppendPayload(MessageAssembler assembler, in PayloadRange range, ref ContentHasher hasher)
    {
        if (!range.IsNone && assembler.TryGetPayload(range, out var bytes))
            hasher.Append(bytes);
    }

    // The data runs of each flow, client to server first, so the digest does not depend on which end tapped them.
    private static ulong PipeDigest(MessageAssembler assembler, NntlSessionRole role, int pipe)
    {
        var hasher = new ContentHasher();
        for (var f = 0; f < 2; f++)
        {
            var direction = DirectionOf(role, (WireFlow)f);
            foreach (var run in assembler.GetPipeRuns(pipe, direction))
            {
                var message = assembler.Messages[run];
                AppendPayload(assembler, new PayloadRange(message.Ref, 0, message.Length), ref hasher);
            }

            hasher.Boundary((ulong)f + 1);
        }

        return hasher.Finish();
    }

    private static EntityOutcome OutcomeOf(InvocationState state) => state switch
    {
        InvocationState.Pending => EntityOutcome.Pending,
        InvocationState.Completed => EntityOutcome.Completed,
        InvocationState.Exception => EntityOutcome.Exception,
        InvocationState.Canceled => EntityOutcome.Canceled,
        InvocationState.Unauthorized => EntityOutcome.Unauthorized,
        InvocationState.OneWay => EntityOutcome.OneWay,
        _ => EntityOutcome.Refused,
    };

    private static EntityOutcome OutcomeOf(PipeLifecycle lifecycle) => lifecycle switch
    {
        PipeLifecycle.Open => EntityOutcome.Open,
        PipeLifecycle.Refused => EntityOutcome.Refused,
        PipeLifecycle.Closing => EntityOutcome.Closing,
        _ => EntityOutcome.Closed,
    };

    private struct FrameStats
    {
        public long Start;
        public long End;
        public long Bytes;
        public int Frames;

        public void Add(in FrameRecord frame)
        {
            if (Frames == 0 || frame.StartTicks < Start)
                Start = frame.StartTicks;
            if (Frames == 0 || frame.EndTicks > End)
                End = frame.EndTicks;
            Bytes += frame.Length;
            Frames++;
        }

        public readonly EntitySide ToSide(int index, EntityOutcome outcome, int items, ulong digest) => new()
        {
            IsPresent = true,
            Index = index,
            StartTicks = Start,
            EndTicks = End,
            Bytes = Bytes,
            Frames = Frames,
            Items = items,
            Outcome = outcome,
            ContentHash = digest,
        };
    }
}
