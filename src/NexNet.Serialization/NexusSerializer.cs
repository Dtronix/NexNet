using System;
using System.Buffers;

namespace NexNet.Serialization;

/// <summary>
/// Convenience entry points that serialize through the registered formatter for <typeparamref name="T"/>.
/// Usable from async methods, which cannot hold <see cref="MsgPackWriter"/>/<see cref="MsgPackReader"/> locals before C# 13.
/// </summary>
public static class NexusSerializer
{
    /// <summary>
    /// Writes <paramref name="value"/> as one MessagePack value.
    /// </summary>
    public static void Serialize<T>(IBufferWriter<byte> output, T value)
    {
        var writer = new MsgPackWriter(output);
        NexusFormatterRegistry.Get<T>().Serialize(ref writer, value);
        writer.Flush();
    }

    /// <summary>
    /// Serializes <paramref name="value"/> into a new array.
    /// </summary>
    public static byte[] Serialize<T>(T value)
    {
        var buffer = PooledArrayBufferWriter.Rent();
        try
        {
            Serialize(buffer, value);
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Return();
        }
    }

    /// <summary>
    /// Reads one MessagePack value.
    /// </summary>
    public static T? Deserialize<T>(ReadOnlyMemory<byte> data, NexusSerializerOptions? options = null)
    {
        var reader = new MsgPackReader(data, options);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
    }

    /// <summary>
    /// Reads one MessagePack value.
    /// </summary>
    public static T? Deserialize<T>(in ReadOnlySequence<byte> data, NexusSerializerOptions? options = null)
    {
        var reader = new MsgPackReader(data, options);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
    }
}
