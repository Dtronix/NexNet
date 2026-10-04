using System;
using System.Buffers;
using System.Runtime.InteropServices;
using NexNet.Pools;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// Base interface for all messages.
/// </summary>
/// <remarks>
/// Every message body is a fixed-length MessagePack array written and read by hand-written code.
/// Readers require the exact element count; the protocol version gates any change to a message.
/// </remarks>
internal interface IMessageBase : IDisposable
{
    /// <summary>
    /// Type of the message.
    /// </summary>
    public static abstract MessageType Type { get; }

    public IPooledMessage? MessageCache { set; }

    /// <summary>
    /// Writes this message's MessagePack body.
    /// </summary>
    void Serialize(ref MsgPackWriter writer);

    /// <summary>
    /// Populates this (pooled) instance from a MessagePack body.
    /// </summary>
    void Deserialize(ref MsgPackReader reader);

    protected static void ReturnPooledMemory<T>(Memory<T> memory) => ReturnPooledMemory((ReadOnlyMemory<T>)memory);
    protected static void ReturnPooledMemory<T>(ReadOnlyMemory<T> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is { Length: > 0 })
        {
            ArrayPool<T>.Shared.Return(segment.Array, false);
        }
    }

    /// <summary>
    /// Copies an embedded MessagePack value (exactly one value, validated by skipping) into a pooled array.
    /// </summary>
    protected static Memory<byte> ReadEmbeddedValueToPooled(ref MsgPackReader reader)
    {
        var raw = reader.ReadRawValue();
        var length = checked((int)raw.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        raw.CopyTo(rented);
        return rented.AsMemory(0, length);
    }
}
