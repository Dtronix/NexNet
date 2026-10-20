using System;
using System.Buffers.Binary;
using NexNet.Messages;
using NexNet.Serialization;

namespace NexNet.Internals;

/// <summary>
/// Builds a complete frame (type, body length, body) outside a session, so one encoding can be copied to several
/// sessions.
/// </summary>
internal static class MessageFrame
{
    /// <summary>
    /// Type byte and body length.
    /// </summary>
    public const int HeaderLength = 3;

    /// <summary>
    /// Writes <paramref name="message"/> as one frame to <paramref name="output"/>. Returns false, leaving
    /// <paramref name="output"/> empty, if the body is larger than one frame.
    /// </summary>
    public static bool TryWrite<TMessage>(TMessage message, PooledArrayBufferWriter output)
        where TMessage : IMessageBase
    {
        output.GetSpan(HeaderLength);
        output.Advance(HeaderLength);

        var writer = new MsgPackWriter(output);
        message.Serialize(ref writer);
        writer.Flush();

        var bodyLength = output.WrittenCount - HeaderLength;
        if (bodyLength > ushort.MaxValue)
        {
            output.Clear();
            return false;
        }

        var header = output.WrittenMemory.Span;
        header[0] = (byte)TMessage.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(1, 2), (ushort)bodyLength);
        return true;
    }
}
