using System.Buffers;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// A snapshot of an invocation result's fields, taken once. Its <see cref="Length"/> and the bytes
/// <see cref="Write"/> produces come from the same values, so a session can write the frame header before the body.
/// The only encoder of <see cref="InvocationResultMessage"/> bodies.
/// </summary>
internal readonly struct InvocationResultBody : IFrameBody
{
    public readonly ushort InvocationId;
    public readonly InvocationResultMessage.StateType State;

    /// <summary>
    /// The result: exactly one MessagePack value, or null for none.
    /// </summary>
    public readonly ReadOnlySequence<byte>? Result;

    private readonly int _length;

    public InvocationResultBody(InvocationResultMessage message)
    {
        InvocationId = message.InvocationId;
        State = message.State;
        Result = message.Result;
        var length = 1 // array header
                     + MsgPackWriter.GetUInt32CompactLength(InvocationId)
                     + MsgPackWriter.GetUInt32CompactLength((byte)State);
        if (Result != null)
            length += checked((int)Result.Value.Length);
        _length = length;
    }

    /// <inheritdoc />
    public int Length => _length;

    /// <summary>
    /// Writes the body: <c>[invocationId, state]</c>, or <c>[invocationId, state, result]</c> when a result is
    /// present.
    /// </summary>
    public void Write(ref MsgPackWriter writer)
    {
        if (Result == null)
        {
            writer.WriteArrayHeader(2);
            writer.Write(InvocationId);
            writer.Write((byte)State);
            return;
        }

        writer.WriteArrayHeader(3);
        writer.Write(InvocationId);
        writer.Write((byte)State);
        writer.WriteRaw(Result.Value);
    }
}
