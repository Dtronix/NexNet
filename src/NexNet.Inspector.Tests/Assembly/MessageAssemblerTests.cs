using System.Buffers;
using NexNet.Collections;
using NexNet.Inspector.Core.Assembly;
using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using static NexNet.Inspector.Core.Model.TapDirection;

namespace NexNet.Inspector.Tests.Assembly;

public class MessageAssemblerTests
{
    private static readonly MethodResolver Resolver = new(new Dictionary<ushort, PipeContent>
    {
        [Scenarios.ChannelMethod] = PipeContent.Channel,
        [Scenarios.CollectionMethod] = PipeContent.Collection,
    });

    [Test]
    public async Task SendAttachment_reassembles_five_chunks_interleaved_with_a_pipe_and_a_ping()
    {
        var (script, arguments) = Scenarios.SendAttachment();
        var assembler = AssemblyHarness.Run(script);

        await Assert.That(assembler.Role).IsEqualTo(NntlSessionRole.Client);

        var large = assembler.Messages.ToArray().Single(m => m.Kind == AssembledMessageKind.Large);
        await Assert.That(large.State).IsEqualTo(AssemblyState.Complete);
        await Assert.That(large.FragmentCount).IsEqualTo(6);
        await Assert.That(large.Length).IsEqualTo(large.TotalLength);
        await Assert.That(large.InnerType).IsEqualTo(MessageType.Invocation);

        var fragments = new List<TraceFragment>();
        await Assert.That(assembler.GetFragments(large.Ref, fragments)).IsEqualTo(6);
        await Assert.That(fragments[0].Length).IsEqualTo(0);
        long expectedOffset = 0;
        foreach (var fragment in fragments)
        {
            await Assert.That(fragment.MessageOffset).IsEqualTo(expectedOffset);
            expectedOffset += fragment.Length;
        }

        await Assert.That(expectedOffset).IsEqualTo(large.TotalLength);

        var interleaved = new List<FrameRecord>();
        assembler.GetInterleaved(large.Ref, interleaved);
        await Assert.That(interleaved.Select(f => f.Type).ToArray())
            .IsEquivalentTo(new[] { MessageType.DuplexPipeWrite, MessageType.Ping, MessageType.DuplexPipeWrite });

        // N marks exactly the large message's frames: not the pipe writes (each a run of one) or the ping.
        foreach (var frame in assembler.GetFrames(Outbound).ToArray())
        {
            var split = frame.Type is MessageType.MessageStart or MessageType.MessageChunk;
            await Assert.That((frame.Flags & FrameFlags.NexNetSplit) != 0).IsEqualTo(split).Because(frame.Type.ToString());
            await Assert.That((frame.Flags & FrameFlags.Cancel) != 0).IsFalse();
        }

        var invocations = assembler.Invocations.ToArray();
        await Assert.That(invocations.Length).IsEqualTo(2);
        var opener = invocations[0];
        await Assert.That(opener.MethodId).IsEqualTo(Scenarios.UploadMethod);
        await Assert.That(opener.State).IsEqualTo(InvocationState.OneWay);
        await Assert.That(opener.Pipe).IsEqualTo(0);

        var attachment = invocations[1];
        await Assert.That(attachment.InvocationId).IsEqualTo((ushort)5);
        await Assert.That(attachment.MethodId).IsEqualTo(Scenarios.AttachmentMethod);
        await Assert.That(attachment.Direction).IsEqualTo(Outbound);
        await Assert.That(attachment.State).IsEqualTo(InvocationState.Completed);
        await Assert.That(attachment.Request).IsEqualTo(large.Ref);
        await Assert.That(attachment.Result.Kind).IsEqualTo(MessageRefKind.Frame);
        await Assert.That(Payload(assembler, attachment.Arguments)).IsEquivalentTo(arguments);
        await Assert.That(Payload(assembler, attachment.Value)).IsEquivalentTo(new byte[] { 0xC0 });
        foreach (var fragment in fragments)
            await Assert.That(assembler.GetLink(Outbound, fragment.Seq).EntityIndex).IsEqualTo(1);

        var pipe = assembler.Pipes.ToArray().Single();
        await Assert.That(pipe.PipeId).IsEqualTo(Scenarios.UploadPipe);
        await Assert.That(pipe.OpenerInvocation).IsEqualTo(0);
        await Assert.That(pipe.OpenerDirection).IsEqualTo(Outbound);
        await Assert.That(pipe.Lifecycle).IsEqualTo(PipeLifecycle.Closed);
        await Assert.That(pipe.OutboundBytes).IsEqualTo(150);
        await Assert.That(pipe.OutboundRuns).IsEqualTo(2);
        await Assert.That(pipe.InboundGrants).IsEqualTo(262144);
        await Assert.That(pipe.OutboundGrants).IsEqualTo(262144);
        await Assert.That(assembler.PipeEvents.Length).IsEqualTo(4);

        var greetings = assembler.Greetings.ToArray();
        await Assert.That(greetings.Length).IsEqualTo(2);
        await Assert.That(greetings[0].Kind).IsEqualTo(GreetingKind.Client);
        await Assert.That(greetings[0].Version).IsEqualTo("v1");
        await Assert.That(Payload(assembler, greetings[0].Token)).IsEquivalentTo(Scenarios.Token);
        await Assert.That(greetings[1].Kind).IsEqualTo(GreetingKind.Server);
        await Assert.That(greetings[1].ClientId).IsEqualTo(42);
        await Assert.That(greetings[1].IdleTimeoutMs).IsEqualTo(30000u);
    }

    [Test]
    public async Task Aborts_end_refused_and_canceled_argument_streams()
    {
        var assembler = AssemblyHarness.Run(Scenarios.Aborts());
        var messages = assembler.Messages.ToArray();
        await Assert.That(messages.Length).IsEqualTo(2);

        var refused = messages[0];
        await Assert.That(refused.State).IsEqualTo(AssemblyState.Aborted);
        await Assert.That(refused.AbortReason).IsEqualTo(MessageAbortReason.Busy);
        await Assert.That(refused.FragmentCount).IsEqualTo(5);
        var fragments = new List<TraceFragment>();
        assembler.GetFragments(refused.Ref, fragments);
        await Assert.That(fragments.Select(f => f.Direction).ToArray())
            .IsEquivalentTo(new[] { Outbound, Outbound, Inbound, Outbound, Outbound });
        var interleaved = new List<FrameRecord>();
        await Assert.That(assembler.GetInterleaved(refused.Ref, interleaved)).IsEqualTo(0);

        var refusedCall = assembler.Invocations[refused.Invocation];
        await Assert.That(refusedCall.State).IsEqualTo(InvocationState.Refused);
        await Assert.That(refusedCall.MethodId).IsEqualTo((ushort)8);
        await Assert.That((assembler.GetFrames(Inbound)[0].Flags & FrameFlags.NexNetSplit) != 0).IsTrue();

        var canceled = messages[1];
        await Assert.That(canceled.State).IsEqualTo(AssemblyState.Aborted);
        await Assert.That(canceled.AbortReason).IsEqualTo(MessageAbortReason.Cancelled);
        var canceledCall = assembler.Invocations[canceled.Invocation];
        await Assert.That(canceledCall.State).IsEqualTo(InvocationState.Canceled);

        var outbound = assembler.GetFrames(Outbound).ToArray();
        var abort = outbound[^1];
        await Assert.That(abort.Type).IsEqualTo(MessageType.MessageAbort);
        await Assert.That(abort.Flags & (FrameFlags.NexNetSplit | FrameFlags.Cancel))
            .IsEqualTo(FrameFlags.NexNetSplit | FrameFlags.Cancel);
        assembler.GetInterleaved(canceled.Ref, interleaved);
        await Assert.That(interleaved.Single().Type).IsEqualTo(MessageType.Ping);
        await Assert.That((outbound[^2].Flags & FrameFlags.NexNetSplit) != 0).IsFalse();
    }

    [Test]
    public async Task Channel_items_crossing_frames_are_split_and_flagged()
    {
        var assembler = AssemblyHarness.Run(Scenarios.Channel(), resolver: Resolver);
        var (outboundValues, inboundValues) = Scenarios.ChannelValues();

        var pipe = assembler.Pipes.ToArray().Single();
        await Assert.That(pipe.Content).IsEqualTo(PipeContent.Channel);
        await Assert.That(pipe.ChannelUnavailable).IsFalse();
        await Assert.That(pipe.OpenerInvocation).IsEqualTo(0);

        var items = assembler.ChannelItems.ToArray();
        var outboundItems = items.Where(i => i.Direction == Outbound).ToArray();
        var inboundItems = items.Where(i => i.Direction == Inbound).ToArray();
        await Assert.That(outboundItems.Length).IsEqualTo(outboundValues.Length);
        await Assert.That(inboundItems.Length).IsEqualTo(inboundValues.Length);
        for (var i = 0; i < outboundValues.Length; i++)
        {
            await Assert.That(outboundItems[i].State).IsEqualTo(AssemblyState.Complete);
            await Assert.That(Payload(assembler, outboundItems[i].Value)).IsEquivalentTo(outboundValues[i]);
        }

        for (var i = 0; i < inboundValues.Length; i++)
            await Assert.That(Payload(assembler, inboundItems[i].Value)).IsEquivalentTo(inboundValues[i]);

        await Assert.That(outboundItems.Select(i => i.FragmentCount).ToArray()).IsEquivalentTo(new[] { 1, 4, 1, 1 });
        await Assert.That(inboundItems.Select(i => i.FragmentCount).ToArray()).IsEquivalentTo(new[] { 1, 1 });

        // Every outbound data frame carries part of the second item; the inbound frame holds whole items only.
        foreach (var frame in assembler.GetFrames(Outbound).ToArray().Where(f => f.Type == MessageType.DuplexPipeWrite))
            await Assert.That((frame.Flags & FrameFlags.NexNetSplit) != 0).IsTrue();
        foreach (var frame in assembler.GetFrames(Inbound).ToArray())
            await Assert.That((frame.Flags & FrameFlags.NexNetSplit) != 0).IsFalse();

        var interleaved = new List<FrameRecord>();
        assembler.GetInterleaved(outboundItems[1].Ref, interleaved);
        await Assert.That(interleaved.Single().Type).IsEqualTo(MessageType.Ping);
    }

    [Test]
    public async Task Channel_switched_on_after_the_data_replays_to_the_same_items()
    {
        var live = AssemblyHarness.Run(Scenarios.Channel(), resolver: Resolver);
        var late = AssemblyHarness.Run(Scenarios.Channel(), beforeComplete: a => a.SetPipeContent(0, PipeContent.Channel));
        var raw = AssemblyHarness.Run(Scenarios.Channel());

        await Assert.That(raw.ChannelItems.Length).IsEqualTo(0);
        await Assert.That(ItemKeys(late)).IsEquivalentTo(ItemKeys(live));
        await Assert.That(FrameKeys(late)).IsEquivalentTo(FrameKeys(live));
    }

    [Test]
    public async Task Cancellation_and_canceled_results_are_flagged_X()
    {
        var assembler = AssemblyHarness.Run(Scenarios.Cancellation());
        var invocations = assembler.Invocations.ToArray();
        await Assert.That(invocations.Length).IsEqualTo(5);

        var canceled = invocations[0];
        await Assert.That(canceled.State).IsEqualTo(InvocationState.Canceled);
        await Assert.That(canceled.ResultState).IsEqualTo(InvocationResultState.Canceled);
        await Assert.That(canceled.CancellationSeq).IsEqualTo(1);
        await Assert.That(assembler.GetFrames(Outbound)[1].Flags).IsEqualTo(FrameFlags.Cancel);
        await Assert.That(assembler.TryGetFrame(Inbound, canceled.Result.Seq, out var result)).IsTrue();
        await Assert.That(result.Flags).IsEqualTo(FrameFlags.Cancel);
        await Assert.That(canceled.Value.IsNone).IsTrue();

        await Assert.That(invocations[1].State).IsEqualTo(InvocationState.Exception);
        await Assert.That(Payload(assembler, invocations[1].Value).Length).IsGreaterThan(0);
        await Assert.That(invocations[2].State).IsEqualTo(InvocationState.Unauthorized);

        // The peer's call #4 is a different invocation from ours.
        var incoming = invocations[3];
        await Assert.That(incoming.Direction).IsEqualTo(Inbound);
        await Assert.That(incoming.MethodId).IsEqualTo((ushort)9);
        await Assert.That(incoming.State).IsEqualTo(InvocationState.Completed);
        await Assert.That(Payload(assembler, incoming.Value)).IsEquivalentTo(new byte[] { 42 });

        var orphan = invocations[4];
        await Assert.That(orphan.HasRequest).IsFalse();
        await Assert.That(orphan.InvocationId).IsEqualTo((ushort)99);
        var orphanFrame = assembler.GetFrames(Inbound).ToArray()[^1];
        await Assert.That(orphanFrame.Flags).IsEqualTo(FrameFlags.Cancel);
        await Assert.That(assembler.GetLink(Inbound, orphanFrame.Seq).Flags).IsEqualTo(FrameLinkFlags.Orphan);

        var xFrames = assembler.GetFrames(Outbound).ToArray().Concat(assembler.GetFrames(Inbound).ToArray())
            .Count(f => (f.Flags & FrameFlags.Cancel) != 0);
        await Assert.That(xFrames).IsEqualTo(3);
    }

    [Test]
    public async Task Collection_messages_decode_every_golden_item()
    {
        var (script, vectors) = Scenarios.Collection();
        var assembler = AssemblyHarness.Run(script, resolver: Resolver);

        await Assert.That(assembler.Role).IsEqualTo(NntlSessionRole.Server);
        var collection = assembler.Collections.ToArray().Single();
        await Assert.That(collection.CollectionId).IsEqualTo(Scenarios.CollectionMethod);
        await Assert.That(collection.MessageCount).IsEqualTo(vectors.Count + 1);

        var pipe = assembler.Pipes[collection.Pipe];
        await Assert.That(pipe.PipeId).IsEqualTo(Scenarios.CollectionPipe);
        await Assert.That(pipe.OpenerDirection).IsEqualTo(Inbound);
        await Assert.That(pipe.Content).IsEqualTo(PipeContent.Collection);

        var messages = assembler.CollectionMessages.ToArray();
        for (var i = 0; i < vectors.Count; i++)
        {
            var (id, _, value) = vectors[i];
            var message = messages[i];
            var body = value[1];
            var kind = (CollectionMessageKind)value[0].GetInt32();
            await Assert.That(message.Malformed).IsFalse().Because(id);
            await Assert.That(message.Direction).IsEqualTo(Outbound).Because(id);
            await Assert.That(message.Kind).IsEqualTo(kind).Because(id);
            await Assert.That(message.Flags).IsEqualTo((NexusCollectionMessageFlags)body[0].GetInt32()).Because(id);

            int[] versioned = [0, 3, 4, 5, 6, 7];
            var expectedVersion = versioned.Contains((int)kind) ? body[1].GetInt32() : -1;
            await Assert.That(message.Version).IsEqualTo(expectedVersion).Because(id);
            switch (kind)
            {
                case CollectionMessageKind.ResetStart:
                    await Assert.That(message.TotalValues).IsEqualTo(body[2].GetInt32()).Because(id);
                    break;
                case CollectionMessageKind.Insert:
                case CollectionMessageKind.Replace:
                    await Assert.That(message.Position).IsEqualTo(body[2].GetInt32()).Because(id);
                    await Assert.That(Payload(assembler, message.Value).Length).IsGreaterThan(0).Because(id);
                    break;
                case CollectionMessageKind.Move:
                    await Assert.That(message.Position).IsEqualTo(body[2].GetInt32()).Because(id);
                    await Assert.That(message.ToPosition).IsEqualTo(body[3].GetInt32()).Because(id);
                    break;
                case CollectionMessageKind.Remove:
                    await Assert.That(message.Position).IsEqualTo(body[2].GetInt32()).Because(id);
                    break;
                case CollectionMessageKind.ResetValues:
                    await Assert.That(Payload(assembler, message.Value).Length).IsGreaterThan(0).Because(id);
                    break;
            }
        }

        var request = messages[^1];
        await Assert.That(request.Direction).IsEqualTo(Inbound);
        await Assert.That(request.Kind).IsEqualTo(CollectionMessageKind.Insert);
        await Assert.That(Payload(assembler, request.Value)).IsEquivalentTo(new byte[] { 5 });
    }

    [Test]
    public async Task Greetings_from_golden_vectors_record_the_token_range()
    {
        var vectors = GoldenVectors.Load("messages.jsonl")
            .Where(v => v.Id.StartsWith("msg.client-greeting", StringComparison.Ordinal)
                        || v.Id.StartsWith("msg.server-greeting", StringComparison.Ordinal))
            .ToList();
        await Assert.That(vectors.Count).IsEqualTo(8);

        foreach (var (id, bytes, value) in vectors)
        {
            var server = id.StartsWith("msg.server", StringComparison.Ordinal);
            var type = server ? MessageType.ServerGreeting
                : id.Contains("reconnection", StringComparison.Ordinal) ? MessageType.ClientGreetingReconnection
                : MessageType.ClientGreeting;
            var assembler = AssemblyHarness.Run(new WireScript().Frame(server ? Inbound : Outbound, type, bytes));
            var greeting = assembler.Greetings.ToArray().Single();
            await Assert.That(assembler.Role).IsEqualTo(NntlSessionRole.Client).Because(id);

            if (server)
            {
                await Assert.That(greeting.Kind).IsEqualTo(GreetingKind.Server).Because(id);
                await Assert.That(greeting.ClientNexusHash).IsEqualTo(value[0].GetInt32()).Because(id);
                await Assert.That(greeting.ClientId).IsEqualTo(value[1].GetInt64()).Because(id);
                await Assert.That(greeting.IdleTimeoutMs).IsEqualTo(value[4].GetUInt32()).Because(id);
                continue;
            }

            await Assert.That(greeting.ServerNexusHash).IsEqualTo(value[1].GetInt32()).Because(id);
            await Assert.That(greeting.ClientNexusHash).IsEqualTo(value[2].GetInt32()).Because(id);
            await Assert.That(greeting.MaxMessageSize).IsEqualTo(value[4].GetInt32()).Because(id);
            var token = Convert.FromBase64String(value[3].GetString()!);
            var nil = id.EndsWith("nil-token", StringComparison.Ordinal);
            await Assert.That(greeting.Token.IsNone).IsEqualTo(nil).Because(id);
            if (!nil)
                await Assert.That(Payload(assembler, greeting.Token)).IsEquivalentTo(token).Because(id);

            await Assert.That(MessageAssembler.TryFindGreetingToken(bytes, out var offset, out var length)).IsTrue();
            await Assert.That(length).IsEqualTo(nil ? -1 : token.Length).Because(id);
            if (!nil)
                await Assert.That(offset).IsEqualTo(greeting.Token.Offset).Because(id);
        }
    }

    [Test]
    public async Task Every_golden_message_body_reads_without_error()
    {
        var script = new WireScript();
        var count = 0;
        foreach (var frame in Framing.GoldenFrames.Messages)
        {
            script.Frames.Add((Outbound, frame.Bytes));
            count++;
        }

        var assembler = AssemblyHarness.Run(script);
        var frames = assembler.GetFrames(Outbound).ToArray();
        await Assert.That(frames.Length).IsEqualTo(count);
        foreach (var frame in frames)
        {
            var flags = assembler.GetLink(Outbound, frame.Seq).Flags;
            await Assert.That(flags & (FrameLinkFlags.BodyMalformed | FrameLinkFlags.BytesUnavailable))
                .IsEqualTo(FrameLinkFlags.None).Because(frame.Type.ToString());
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(42)]
    [Arguments(20261122)]
    public async Task Random_rechunking_gives_identical_assembly(int seed)
    {
        WireScript[] scripts =
        [
            Scenarios.SendAttachment().Script,
            Scenarios.Aborts(),
            Scenarios.Channel(),
            Scenarios.Cancellation(),
            Scenarios.Collection().Script,
        ];

        var random = new Random(seed);
        foreach (var script in scripts)
        {
            var baseline = AssemblyHarness.Run(script, resolver: Resolver);
            for (var round = 0; round < 3; round++)
            {
                var rechunked = AssemblyHarness.Run(script, random, Resolver);
                await AssertSameAssembly(baseline, rechunked);
            }
        }
    }

    [Test]
    public async Task One_byte_ops_give_identical_assembly()
    {
        var script = Scenarios.Collection().Script;
        var baseline = AssemblyHarness.Run(script, resolver: Resolver);
        var assembler = new MessageAssembler(new InMemoryStreamBytes(), new InMemoryStreamBytes());
        await Assert.That(assembler.Role).IsNull();

        var oneByte = AssemblyHarness.Run(script, new OneByteRandom(), Resolver);
        await AssertSameAssembly(baseline, oneByte);
    }

    private static async Task AssertSameAssembly(MessageAssembler expected, MessageAssembler actual)
    {
        await Assert.That(actual.Role).IsEqualTo(expected.Role);
        await Assert.That(FrameKeys(actual)).IsEquivalentTo(FrameKeys(expected));
        await Assert.That(actual.Messages.ToArray()).IsEquivalentTo(expected.Messages.ToArray());
        await Assert.That(actual.Invocations.ToArray()).IsEquivalentTo(expected.Invocations.ToArray());
        await Assert.That(actual.Pipes.ToArray()).IsEquivalentTo(expected.Pipes.ToArray());
        await Assert.That(actual.PipeEvents.ToArray()).IsEquivalentTo(expected.PipeEvents.ToArray());
        await Assert.That(actual.ChannelItems.ToArray()).IsEquivalentTo(expected.ChannelItems.ToArray());
        await Assert.That(actual.Collections.ToArray()).IsEquivalentTo(expected.Collections.ToArray());
        await Assert.That(actual.CollectionMessages.ToArray()).IsEquivalentTo(expected.CollectionMessages.ToArray());
        await Assert.That(actual.Greetings.ToArray()).IsEquivalentTo(expected.Greetings.ToArray());

        foreach (var direction in new[] { Inbound, Outbound })
        {
            foreach (var frame in expected.GetFrames(direction).ToArray())
                await Assert.That(actual.GetLink(direction, frame.Seq)).IsEqualTo(expected.GetLink(direction, frame.Seq));
        }

        foreach (var message in expected.Messages.ToArray())
            await Assert.That(FragmentKeys(actual, message.Ref)).IsEquivalentTo(FragmentKeys(expected, message.Ref));
        foreach (var item in expected.ChannelItems.ToArray())
            await Assert.That(FragmentKeys(actual, item.Ref)).IsEquivalentTo(FragmentKeys(expected, item.Ref));
        // An aborted stream's arguments are incomplete, so both sides must agree on availability too.
        foreach (var invocation in expected.Invocations.ToArray())
        {
            await Assert.That(TryPayload(actual, invocation.Arguments)).IsEquivalentTo(TryPayload(expected, invocation.Arguments));
            await Assert.That(TryPayload(actual, invocation.Value)).IsEquivalentTo(TryPayload(expected, invocation.Value));
        }
    }

    // Frames without what depends on op boundaries: the T flag, op ids and ticks.
    private static FrameRecord[] FrameKeys(MessageAssembler assembler)
        => assembler.GetFrames(Inbound).ToArray().Concat(assembler.GetFrames(Outbound).ToArray())
            .Select(f => f with
            {
                Flags = f.Flags & ~FrameFlags.TransportSplit,
                FirstOpId = 0,
                LastOpId = 0,
                StartTicks = 0,
                EndTicks = 0,
            })
            .ToArray();

    private static (TapDirection, long, long, long, int)[] FragmentKeys(MessageAssembler assembler, MessageRef message)
    {
        var fragments = new List<TraceFragment>();
        assembler.GetFragments(message, fragments);
        return fragments.Select(f => (f.Direction, f.Seq, f.MessageOffset, f.StreamOffset, f.Length)).ToArray();
    }

    // Items in stream order per direction, without list positions (replay adds them direction by direction).
    private static (TapDirection, long, int, int, AssemblyState, int, string)[] ItemKeys(MessageAssembler assembler)
        => assembler.ChannelItems.ToArray()
            .OrderBy(i => i.Direction).ThenBy(i => i.PipeOffset)
            .Select(i => (i.Direction, i.PipeOffset, i.PrefixLength, i.ValueLength, i.State, i.FragmentCount,
                string.Join(",", FragmentKeys(assembler, i.Ref))))
            .ToArray();

    private static byte[] Payload(MessageAssembler assembler, PayloadRange range)
    {
        if (range.IsNone)
            return [];
        if (!assembler.TryGetPayload(range, out var bytes))
            throw new InvalidOperationException("Payload bytes unavailable.");
        return bytes.ToArray();
    }

    private static byte[] TryPayload(MessageAssembler assembler, PayloadRange range)
        => assembler.TryGetPayload(range, out var bytes) ? bytes.ToArray() : [0xFF, 0xFF, 0xFF];

    private sealed class OneByteRandom() : Random(0)
    {
        public override int Next(int minValue, int maxValue) => minValue;

        public override int Next(int maxValue) => 0;
    }
}
