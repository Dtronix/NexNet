using System.Buffers;
using System.Runtime.InteropServices;
using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Assembly;

public sealed partial class MessageAssembler
{
    private void OnInvocation(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var reader = new MsgPackReader(body);
        reader.ReadArrayHeader(4);
        var id = reader.ReadUInt16();
        var methodId = reader.ReadUInt16();
        var flags = (InvocationFlags)reader.ReadByte();
        var argumentsOffset = reader.Consumed;
        reader.Skip();

        var request = MessageRef.ForFrame(frame.Direction, frame.Seq);
        // An IgnoreReturn ID means nothing and is usually 0 (spec §4.3); keying it would shadow a real pending call.
        var invocation = NewInvocation(frame.Direction, id, request, keyed: (flags & InvocationFlags.IgnoreReturn) == 0);
        SetLink(frame.Direction, frame.Seq, EntityKind.Invocation, invocation, -1);
        SetRequestEnvelope(invocation, methodId, flags, new PayloadRange(request, argumentsOffset, body.Length - argumentsOffset));
        FinishRequest(invocation, body.Slice((int)argumentsOffset));
    }

    private void OnInvocationResult(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var result = MessageRef.ForFrame(frame.Direction, frame.Seq);
        if (!TryReadResultEnvelope(body, result, out var id, out var state, out var value))
        {
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.BodyMalformed);
            return;
        }

        var invocation = FindOrAddForResult(Opposite(frame.Direction), id, out var orphan);
        SetLink(frame.Direction, frame.Seq, EntityKind.Invocation, invocation, -1);
        if (orphan)
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.Orphan);
        ApplyResult(invocation, state, result, value);
    }

    private void OnInvocationCancellation(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var reader = new MsgPackReader(body);
        reader.ReadArrayHeader(1);
        var id = reader.ReadUInt16();

        // The caller cancels, so the cancellation travels the way the request did.
        var invocation = FindOrAddForResult(frame.Direction, id, out var orphan);
        SetLink(frame.Direction, frame.Seq, EntityKind.Invocation, invocation, -1);
        if (orphan)
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.Orphan);

        ref var row = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
        if (row.CancellationSeq < 0)
            row = row with { CancellationSeq = frame.Seq };
        AddFrameFlags(frame.Direction, frame.Seq, FrameFlags.Cancel);
    }

    private void OnMessageStart(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var reader = new MsgPackReader(body);
        reader.ReadArrayHeader(4);
        var id = reader.ReadUInt16();
        var kind = (LargeMessageKind)reader.ReadByte();
        var innerType = (MessageType)reader.ReadByte();
        var total = reader.ReadUInt32();
        if (kind > LargeMessageKind.Result)
        {
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.BodyMalformed);
            return;
        }

        var key = StreamKey(frame.Direction, kind, id);
        // A second start for an active stream is a protocol error (§6.4); the first one will never complete.
        if (_activeStreams.Remove(key, out var stale))
            SetMessageState(stale, AssemblyState.Truncated);

        var message = NewMessage(new AssembledMessage
        {
            Kind = AssembledMessageKind.Large,
            Direction = frame.Direction,
            State = AssemblyState.Assembling,
            LargeKind = kind,
            InvocationId = id,
            InnerType = innerType,
            TotalLength = total,
            FirstSeq = frame.Seq,
            LastSeq = frame.Seq,
            Invocation = -1,
            Pipe = -1,
            AbortReason = MessageAbortReason.None,
        });
        _activeStreams[key] = message;
        var messageRef = _messages[message].Ref;

        int invocation;
        bool orphan;
        if (kind == LargeMessageKind.Argument)
        {
            invocation = NewInvocation(frame.Direction, id, messageRef, keyed: true);
            orphan = false;
        }
        else
        {
            invocation = FindOrAddForResult(Opposite(frame.Direction), id, out orphan);
            ref var row = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
            row = row with { Result = messageRef };
        }

        CollectionsMarshal.AsSpan(_messages)[message] = _messages[message] with { Invocation = invocation };
        AppendToMessage(message, frame, frame.BodyOffset, 0);
        SetLink(frame.Direction, frame.Seq, EntityKind.Invocation, invocation, message);
        if (orphan)
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.Orphan);
        AddFrameFlags(frame.Direction, frame.Seq, FrameFlags.NexNetSplit);

        if (total == 0)
            FinishLarge(message);
    }

    private void OnMessageChunk(in FrameRecord frame)
    {
        var kind = (LargeMessageKind)frame.ChunkKind;
        if (kind > LargeMessageKind.Result)
        {
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.BodyMalformed);
            return;
        }

        var id = frame.TargetId;
        var key = StreamKey(frame.Direction, kind, id);
        var orphan = false;
        if (!_activeStreams.TryGetValue(key, out var message))
        {
            // Chunks of a stream whose start came before the capture: keep them together, length unknown.
            orphan = true;
            var invocation = -1;
            if (kind == LargeMessageKind.Result
                && _pendingInvocations.TryGetValue(InvocationKey(Opposite(frame.Direction), id), out var pending))
                invocation = pending;

            message = NewMessage(new AssembledMessage
            {
                Kind = AssembledMessageKind.Large,
                Direction = frame.Direction,
                State = AssemblyState.Orphan,
                LargeKind = kind,
                InvocationId = id,
                InnerType = MessageType.Unset,
                TotalLength = -1,
                FirstSeq = frame.Seq,
                LastSeq = frame.Seq,
                Invocation = invocation,
                Pipe = -1,
                AbortReason = MessageAbortReason.None,
            });
            _activeStreams[key] = message;
        }

        AppendToMessage(message, frame, frame.BodyOffset, frame.BodyLength);
        var row = _messages[message];
        SetLink(frame.Direction, frame.Seq, row.Invocation >= 0 ? EntityKind.Invocation : EntityKind.None, row.Invocation, message);
        if (orphan || row.State == AssemblyState.Orphan)
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.Orphan);
        AddFrameFlags(frame.Direction, frame.Seq, FrameFlags.NexNetSplit);

        if (row.State == AssemblyState.Orphan)
            return;

        if (row.LargeKind == LargeMessageKind.Argument && row.Invocation >= 0 && !_invocations[row.Invocation].RequestParsed)
            TryParseArgumentEnvelope(message);

        if (row.Length >= row.TotalLength)
            FinishLarge(message);
    }

    private void OnMessageAbort(in FrameRecord frame)
    {
        if (!TryReadBody(frame, out var body))
            return;

        var reader = new MsgPackReader(body);
        reader.ReadArrayHeader(3);
        var id = reader.ReadUInt16();
        var kind = (LargeMessageKind)reader.ReadByte();
        var reason = (MessageAbortReason)reader.ReadByte();

        // Busy and TooLarge come from the stream's receiver, Cancelled and Ack from its sender (§6.3).
        var fromReceiver = reason is MessageAbortReason.Busy or MessageAbortReason.TooLarge;
        var streamDirection = fromReceiver ? Opposite(frame.Direction) : frame.Direction;
        AddFrameFlags(frame.Direction, frame.Seq, FrameFlags.NexNetSplit);
        if (reason == MessageAbortReason.Cancelled)
            AddFrameFlags(frame.Direction, frame.Seq, FrameFlags.Cancel);

        var key = StreamKey(streamDirection, kind, id);
        if (!_activeStreams.TryGetValue(key, out var message))
        {
            // An abort can race with completion (§6.3); link it to the invocation when that is still known.
            var callerDirection = kind == LargeMessageKind.Argument ? streamDirection : Opposite(streamDirection);
            if (_pendingInvocations.TryGetValue(InvocationKey(callerDirection, id), out var pending))
                SetLink(frame.Direction, frame.Seq, EntityKind.Invocation, pending, -1);
            AddLinkFlags(frame.Direction, frame.Seq, FrameLinkFlags.Orphan);
            return;
        }

        AppendToMessage(message, frame, frame.BodyOffset, 0);
        ref var row = ref CollectionsMarshal.AsSpan(_messages)[message];
        if (row.AbortReason == MessageAbortReason.None)
            row = row with { AbortReason = reason };
        row = row with { State = AssemblyState.Aborted };
        var invocation = row.Invocation;
        SetLink(frame.Direction, frame.Seq, invocation >= 0 ? EntityKind.Invocation : EntityKind.None, invocation, message);

        // After a receiver's refusal the sender's in-flight chunks still belong to the stream, up to its Ack.
        if (!fromReceiver)
            _activeStreams.Remove(key);

        if (invocation < 0)
            return;

        ref var inv = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
        if (inv.State != InvocationState.Pending)
            return;

        if (reason == MessageAbortReason.Cancelled)
        {
            // A canceled streaming argument never runs and gets no result (§4.5).
            inv = inv with { State = InvocationState.Canceled };
            _pendingInvocations.Remove(InvocationKey(inv.Direction, inv.InvocationId));
        }
        else if (fromReceiver)
        {
            inv = inv with { State = InvocationState.Refused };
            _pendingInvocations.Remove(InvocationKey(inv.Direction, inv.InvocationId));
        }
    }

    private void FinishLarge(int message)
    {
        var row = _messages[message];
        var key = StreamKey(row.Direction, row.LargeKind, row.InvocationId);
        if (_activeStreams.TryGetValue(key, out var active) && active == message)
            _activeStreams.Remove(key);

        if (row.State != AssemblyState.Assembling)
            return;

        SetMessageState(message, AssemblyState.Complete);
        if (row.Invocation < 0)
            return;

        if (row.LargeKind == LargeMessageKind.Argument)
        {
            if (!_invocations[row.Invocation].RequestParsed && !TryParseArgumentEnvelope(message))
            {
                AddLinkFlags(row.Direction, row.FirstSeq, FrameLinkFlags.BodyMalformed);
                return;
            }

            var arguments = _invocations[row.Invocation].Arguments;
            if ((_invocations[row.Invocation].Flags & InvocationFlags.DuplexPipe) != 0
                && TryGetPayload(arguments, out var argumentBytes))
                FinishRequest(row.Invocation, argumentBytes);
            else
                FinishRequest(row.Invocation, ReadOnlySequence<byte>.Empty);
            return;
        }

        var peek = (int)Math.Min(PeekLength, row.Length);
        if (!TryPeek(row.Ref, 0, peek, out var envelope)
            || !TryReadResultEnvelope(envelope, row.Ref, out _, out var state, out var value))
        {
            AddLinkFlags(row.Direction, row.FirstSeq, FrameLinkFlags.BodyMalformed);
            return;
        }

        if (!value.IsNone)
            value = value with { Length = row.Length - value.Offset };
        ApplyResult(row.Invocation, state, row.Ref, value);
    }

    // Reads method and flags from the front of a streamed Invocation once enough of it arrived.
    private bool TryParseArgumentEnvelope(int message)
    {
        var row = _messages[message];
        var need = (int)Math.Min(PeekLength, row.TotalLength);
        if (row.Length < need || !TryPeek(row.Ref, 0, need, out var envelope))
            return false;

        try
        {
            var reader = new MsgPackReader(envelope);
            reader.ReadArrayHeader(4);
            reader.ReadUInt16();
            var methodId = reader.ReadUInt16();
            var flags = (InvocationFlags)reader.ReadByte();
            var offset = reader.Consumed;
            SetRequestEnvelope(row.Invocation, methodId, flags, new PayloadRange(row.Ref, offset, row.TotalLength - offset));
            return true;
        }
        catch (NexusSerializationException)
        {
            return false;
        }
    }

    private static bool TryReadResultEnvelope(
        ReadOnlyMemory<byte> body,
        MessageRef message,
        out ushort id,
        out InvocationResultState state,
        out PayloadRange value)
    {
        var reader = new MsgPackReader(body);
        var count = reader.ReadArrayHeader();
        id = 0;
        state = InvocationResultState.Unset;
        value = PayloadRange.None;
        if (count is not (2 or 3))
            return false;

        id = reader.ReadUInt16();
        state = (InvocationResultState)reader.ReadByte();
        if (count == 3)
            value = new PayloadRange(message, reader.Consumed, body.Length - reader.Consumed);
        return true;
    }

    private void SetRequestEnvelope(int invocation, ushort methodId, InvocationFlags flags, PayloadRange arguments)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
        row = row with { MethodId = methodId, Flags = flags, RequestParsed = true, Arguments = arguments };
    }

    // The request is whole: settle what only the flags decide.
    private void FinishRequest(int invocation, ReadOnlyMemory<byte> arguments)
        => FinishRequest(invocation, new ReadOnlySequence<byte>(arguments));

    private void FinishRequest(int invocation, ReadOnlySequence<byte> arguments)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
        if ((row.Flags & InvocationFlags.IgnoreReturn) != 0)
        {
            if (row.State == InvocationState.Pending)
                row = row with { State = InvocationState.OneWay };
            var key = InvocationKey(row.Direction, row.InvocationId);
            if (_pendingInvocations.TryGetValue(key, out var mapped) && mapped == invocation)
                _pendingInvocations.Remove(key);
        }

        if ((row.Flags & InvocationFlags.DuplexPipe) != 0 && !arguments.IsEmpty)
            RegisterPipeOpener(invocation, row.Direction, arguments);
    }

    private void ApplyResult(int invocation, InvocationResultState state, MessageRef result, PayloadRange value)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_invocations)[invocation];
        var newState = state switch
        {
            InvocationResultState.Exception => InvocationState.Exception,
            InvocationResultState.Unauthorized => InvocationState.Unauthorized,
            InvocationResultState.Canceled => InvocationState.Canceled,
            _ => InvocationState.Completed,
        };
        row = row with { State = newState, ResultState = state, Result = result, Value = value };

        var key = InvocationKey(row.Direction, row.InvocationId);
        if (_pendingInvocations.TryGetValue(key, out var mapped) && mapped == invocation)
            _pendingInvocations.Remove(key);

        if (state == InvocationResultState.Canceled)
            AddMessageFlags(result, FrameFlags.Cancel);
    }

    private int NewInvocation(TapDirection caller, ushort id, MessageRef request, bool keyed)
    {
        var index = _invocations.Count;
        _invocations.Add(new InvocationRow
        {
            Index = index,
            InvocationId = id,
            Direction = caller,
            State = InvocationState.Pending,
            Request = request,
            Result = MessageRef.None,
            CancellationSeq = -1,
            Arguments = PayloadRange.None,
            Value = PayloadRange.None,
            Pipe = -1,
        });
        if (keyed)
            _pendingInvocations[InvocationKey(caller, id)] = index;
        return index;
    }

    // The pending invocation a result or cancellation refers to; one without a request when the capture missed it.
    private int FindOrAddForResult(TapDirection caller, ushort id, out bool orphan)
    {
        orphan = !_pendingInvocations.TryGetValue(InvocationKey(caller, id), out var invocation);
        if (orphan)
            invocation = NewInvocation(caller, id, MessageRef.None, keyed: true);
        return invocation;
    }

    private void SetMessageState(int message, AssemblyState state)
    {
        ref var row = ref CollectionsMarshal.AsSpan(_messages)[message];
        row = row with { State = state };
    }

    private static int InvocationKey(TapDirection caller, ushort id) => ((int)caller << 16) | id;

    private static int StreamKey(TapDirection sender, LargeMessageKind kind, ushort id)
        => ((int)sender << 17) | ((int)kind << 16) | id;
}
