using System;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// A snapshot of an invocation's fields, taken once. Its <see cref="Length"/> and the bytes <see cref="Write"/>
/// produces come from the same values, so a session can write the frame header before the body and the two always
/// agree, even if the message is changed while it is being sent.
/// </summary>
internal readonly struct InvocationBody
{
    public readonly ushort InvocationId;
    public readonly ushort MethodId;
    public readonly byte Flags;

    /// <summary>
    /// The serialized arguments (a MessagePack array produced by generated code), or empty for none.
    /// </summary>
    public readonly ReadOnlyMemory<byte> Arguments;

    /// <summary>
    /// Exact number of bytes <see cref="Write"/> writes.
    /// </summary>
    public readonly int Length;

    public InvocationBody(IInvocationMessage message)
    {
        InvocationId = message.InvocationId;
        MethodId = message.MethodId;
        Flags = (byte)message.Flags;
        Arguments = message.Arguments;
        Length = 1 // array header
                 + MsgPackWriter.GetUInt32CompactLength(InvocationId)
                 + MsgPackWriter.GetUInt32CompactLength(MethodId)
                 + MsgPackWriter.GetUInt32CompactLength(Flags)
                 + (Arguments.IsEmpty ? 1 : Arguments.Length);
    }

    /// <summary>
    /// Writes the body: <c>[invocationId, methodId, flags, arguments]</c>.
    /// </summary>
    public void Write(ref MsgPackWriter writer)
    {
        writer.WriteArrayHeader(4);
        writer.Write(InvocationId);
        writer.Write(MethodId);
        writer.Write(Flags);
        if (Arguments.IsEmpty)
            writer.WriteArrayHeader(0); // methods without serialized parameters still send an empty array
        else
            writer.WriteRaw(Arguments.Span); // already a MessagePack array produced by generated code
    }
}
