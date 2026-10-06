using System.Buffers;
using NexNet.Messages;
using NexNet.Serialization;
using NexNet.Serialization.Formatters;

namespace NexNet.IntegrationTests;

/// <summary>
/// Serialization helpers for tests that build or inspect raw protocol bytes.
/// Protocol messages and payloads are both MessagePack; payloads use the registered formatters.
/// </summary>
internal static class TestSerialization
{
    static TestSerialization()
    {
        // Argument tuples are written as MessagePack arrays, the same layout as ValueTuple formatters.
        NexusFormatterRegistry.Register(new ValueTupleFormatter<int>());
        NexusFormatterRegistry.Register(new ValueTupleFormatter<string>());
    }

    public static byte[] SerializeMessage<T>(T message)
        where T : IMessageBase
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        message.Serialize(ref writer);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    public static T DeserializeMessage<T>(ReadOnlySpan<byte> body)
        where T : class, IMessageBase, new()
    {
        var message = new T();
        var reader = new MsgPackReader(body.ToArray());
        message.Deserialize(ref reader);
        if (!reader.End)
            throw new NexusSerializationException("Trailing bytes after message body.");
        return message;
    }

    /// <summary>
    /// Serializes a payload value with its registered formatter.
    /// </summary>
    public static byte[] SerializePayload<T>(T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        NexusFormatterRegistry.Get<T>().Serialize(ref writer, value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Deserializes a payload value with its registered formatter.
    /// </summary>
    public static T? DeserializePayload<T>(in ReadOnlySequence<byte> bytes)
    {
        var reader = new MsgPackReader(bytes);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        return value;
    }

    /// <summary>
    /// Serializes invocation arguments (a ValueTuple, written as a MessagePack array).
    /// </summary>
    public static byte[] SerializeArguments<T>(T arguments) => SerializePayload(arguments);
}
