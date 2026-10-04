using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using NexNet.Serialization;

namespace NexNet.Internals;

/// <summary>
/// Serializes user payload values with the active payload backend (MessagePack, or MemoryPack in legacy builds).
/// Used by runtime paths that only know the type at runtime (collections, results, broadcast arguments).
/// </summary>
internal static class PayloadSerializer
{
    /// <summary>
    /// Serializes a value to a new byte array.
    /// </summary>
    public static byte[] Serialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T value)
    {
#if NEXNET_MEMORYPACK
        return MemoryPack.MemoryPackSerializer.Serialize(value);
#else
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
#endif
    }

    /// <summary>
    /// Deserializes a value.
    /// </summary>
    public static T? Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
    {
#if NEXNET_MEMORYPACK
        return MemoryPack.MemoryPackSerializer.Deserialize<T>(data.Span);
#else
        var reader = new MsgPackReader(data, options);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
#endif
    }

    /// <summary>
    /// Writes an already serialized payload value into a protocol message body: embedded as a MessagePack value,
    /// or wrapped in bin with the MemoryPack payload format.
    /// </summary>
    public static void WriteEmbedded(ref MsgPackWriter writer, ReadOnlySpan<byte> serializedValue)
    {
#if NEXNET_MEMORYPACK
        writer.WriteBinary(serializedValue);
#else
        if (serializedValue.IsEmpty)
            writer.WriteNil();
        else
            writer.WriteRaw(serializedValue);
#endif
    }

    /// <summary>
    /// Reads a payload value written by <see cref="WriteEmbedded"/> into an array rented from <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public static Memory<byte> ReadEmbeddedToPooled(ref MsgPackReader reader)
    {
#if NEXNET_MEMORYPACK
        return reader.ReadBinaryToPooled(out _);
#else
        var raw = reader.ReadRawValue();
        var length = checked((int)raw.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        raw.CopyTo(rented);
        return rented.AsMemory(0, length);
#endif
    }
}
