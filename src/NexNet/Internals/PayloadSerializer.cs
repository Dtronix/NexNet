using System;
using System.Buffers;
using NexNet.Serialization;
using NexNet.Serialization.Formatters;

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
    /// Serializes a run of list values as one `T[]` wire value: the bytes a C# `T[]` of the same items has.
    /// </summary>
    public static byte[] SerializeValues<T>(Memory<T> values)
    {
        var buffer = PooledArrayBufferWriter.Rent();
        try
        {
            var writer = new MsgPackWriter(buffer);
            // Primitive item types register a Memory<T> formatter whose bytes match their T[] form (ext 78, or bin for
            // bytes); keep using it so those lists are unchanged on the wire. Other item types have no Memory<T>
            // formatter, only the item's own, and their T[] form is an array of items.
            if (NexusFormatterRegistry.TryGet<Memory<T>>(out var memoryFormatter))
            {
                memoryFormatter!.Serialize(ref writer, values);
            }
            else
            {
                var element = NexusFormatterRegistry.Get<T>();
                var span = values.Span;
                writer.WriteArrayHeader(span.Length);
                for (var i = 0; i < span.Length; i++)
                    element.Serialize(ref writer, span[i]);
            }

            writer.Flush();
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Return();
        }
    }

    /// <summary>
    /// Deserializes a run of list values written by <see cref="SerializeValues{T}"/>.
    /// </summary>
    public static T[]? DeserializeValues<T>(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
    {
        var reader = new MsgPackReader(data, options);
        T[]? value = null;
        // The generator registers only the item type of a list, not T[], so an item type that no method also uses as
        // an array falls back to the same element-wise ArrayFormatter the generator would register.
        var formatter = NexusFormatterRegistry.TryGet<T[]>(out var registered) ? registered! : FallbackArrayFormatter<T>.Instance;
        formatter.Deserialize(ref reader, ref value);
        return value;
    }

    private static class FallbackArrayFormatter<T>
    {
        public static readonly ArrayFormatter<T> Instance = new();
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
