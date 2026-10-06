using System;
using System.Buffers;
using NexNet.Serialization;

namespace NexNet.Internals;

/// <summary>
/// Serializes user payload values with the registered MessagePack formatters.
/// Used by runtime paths that only know the type at runtime (collections, results, broadcast arguments).
/// </summary>
internal static class PayloadSerializer
{
    /// <summary>
    /// Serializes a value to a new byte array.
    /// </summary>
    public static byte[] Serialize<T>(T value)
    {
        var buffer = PooledArrayBufferWriter.Rent();
        try
        {
            var writer = new MsgPackWriter(buffer);
            NexusFormatterRegistry.Get<T>().Serialize(ref writer, value);
            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Return();
        }
    }

    /// <summary>
    /// Deserializes a value.
    /// </summary>
    public static T? Deserialize<T>(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
    {
        var reader = new MsgPackReader(data, options);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
    }

    /// <summary>
    /// Embeds an already serialized payload value into a protocol message body as one MessagePack value; an empty
    /// payload is written as nil.
    /// </summary>
    public static void WriteEmbedded(ref MsgPackWriter writer, ReadOnlySpan<byte> serializedValue)
    {
        if (serializedValue.IsEmpty)
            writer.WriteNil();
        else
            writer.WriteRaw(serializedValue);
    }

    /// <summary>
    /// Reads a payload value written by <see cref="WriteEmbedded"/> into an array rented from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public static Memory<byte> ReadEmbeddedToPooled(ref MsgPackReader reader)
    {
        var raw = reader.ReadRawValue();
        var length = checked((int)raw.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        raw.CopyTo(rented);
        return rented.AsMemory(0, length);
    }
}
