using NexNet.Inspector.Core.Framing;
using NexNet.Inspector.Core.Model;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Inspector.Core.Assembly;

public sealed partial class MessageAssembler
{
    /// <summary>
    /// Summarizes a frame's body for a one-line label (see <see cref="FrameDetail"/>). Cheap enough to call for every
    /// row as it scrolls into view: it reads assembly's rows, and at most the first few body bytes (the preamble's
    /// version, a credit count, a pipe state), never the whole body. Has no side effects.
    /// </summary>
    /// <param name="direction">The direction.</param>
    /// <param name="seq">The frame's sequence number.</param>
    /// <returns>The detail; <see cref="FrameDetail.None"/> for an unknown or incomplete frame, or one with nothing to add.</returns>
    public FrameDetail GetDetail(TapDirection direction, long seq)
    {
        var index = IndexOf(direction, seq);
        if (index < 0)
            return FrameDetail.None;

        var frame = Frames(direction)[index];
        if ((frame.Flags & FrameFlags.Incomplete) != 0)
            return FrameDetail.None;

        Span<byte> bytes = stackalloc byte[8];
        if (frame.Kind == FrameKind.Preamble)
        {
            // Magic (4), reserved (3), version (1).
            return frame.Length >= FrameLayout.PreambleLength && Store(direction).TryCopyTo(frame.Offset + FrameLayout.PreambleLength - 1, bytes[..1])
                ? new FrameDetail(FrameDetailKind.Preamble, bytes[0])
                : FrameDetail.None;
        }

        if (frame.Kind != FrameKind.Frame)
            return FrameDetail.None;

        var link = Links(direction)[index];
        switch (frame.Type)
        {
            case MessageType.ClientGreeting or MessageType.ClientGreetingReconnection
                when link.Entity == EntityKind.Greeting && (uint)link.EntityIndex < (uint)_greetings.Count:
                var token = _greetings[link.EntityIndex].Token;
                return new FrameDetail(FrameDetailKind.AuthToken, token.IsNone ? -1 : token.Length);
            case MessageType.ServerGreeting when link.Entity == EntityKind.Greeting && (uint)link.EntityIndex < (uint)_greetings.Count:
                return new FrameDetail(FrameDetailKind.ClientId, _greetings[link.EntityIndex].ClientId);
            case MessageType.InvocationResult when link.Entity == EntityKind.Invocation && (uint)link.EntityIndex < (uint)_invocations.Count:
                var state = _invocations[link.EntityIndex].ResultState;
                return state == InvocationResultState.Unset ? FrameDetail.None : new FrameDetail(FrameDetailKind.Result, (long)state);
            case MessageType.MessageStart when (uint)link.Message < (uint)_messages.Count:
                var message = _messages[link.Message];
                return new FrameDetail(FrameDetailKind.MessageStart, (long)message.LargeKind, message.TotalLength);
            case MessageType.InvocationCredit:
                // [credits] as a one-element array: at most 4 bytes.
                if (!TryPeekBody(frame, bytes, out var credit))
                    return FrameDetail.None;
                return TryRead(credit, static (ref MsgPackReader reader) =>
                {
                    reader.ReadArrayHeader(1);
                    return reader.ReadUInt16();
                }, out var credits) ? new FrameDetail(FrameDetailKind.Credit, credits) : FrameDetail.None;
            case MessageType.DuplexPipeUpdateState:
                // [pipeId, state, window]: the state is within the first 5 bytes.
                if (!TryPeekBody(frame, bytes, out var update))
                    return FrameDetail.None;
                return TryRead(update, static (ref MsgPackReader reader) =>
                {
                    reader.ReadArrayHeader();
                    reader.ReadUInt16();
                    return reader.ReadByte();
                }, out var flags) ? new FrameDetail(FrameDetailKind.PipeState, flags) : FrameDetail.None;
            default:
                return FrameDetail.None;
        }
    }

    private delegate long DetailReader(ref MsgPackReader reader);

    // Copies the first bytes of a body (as many as fit) into the detail buffer.
    private bool TryPeekBody(in FrameRecord frame, Span<byte> buffer, out ReadOnlyMemory<byte> body)
    {
        var length = Math.Min(frame.BodyLength, buffer.Length);
        body = default;
        if (length <= 0 || !Store(frame.Direction).TryCopyTo(frame.BodyOffset, buffer[..length]))
            return false;

        // MsgPackReader reads memory, not a span: the copy goes through the assembler's peek buffer.
        buffer[..length].CopyTo(_peek);
        body = _peek.AsMemory(0, length);
        return true;
    }

    private static bool TryRead(ReadOnlyMemory<byte> body, DetailReader read, out long value)
    {
        try
        {
            var reader = new MsgPackReader(body);
            value = read(ref reader);
            return true;
        }
        catch (NexusSerializationException)
        {
            value = 0;
            return false;
        }
    }
}
