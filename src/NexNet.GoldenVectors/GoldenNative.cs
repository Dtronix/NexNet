using NexNet.Serialization;

namespace NexNet.GoldenVectors;

/// <summary>
/// The C# implementation of a vector type: decodes with the NexNet formatter or message codec and encodes the result
/// again, so a vector can be checked against the real C# code.
/// </summary>
public abstract class GoldenNative
{
    /// <summary>The C# type.</summary>
    public abstract Type ClrType { get; }

    /// <summary>
    /// Decodes exactly one value from <paramref name="data"/> and returns its encoding.
    /// </summary>
    /// <exception cref="NexusSerializationException">The data is rejected, or bytes are left after the value.</exception>
    public abstract byte[] Reencode(ReadOnlyMemory<byte> data, NexusSerializerOptions options);

    /// <summary>The trailing-bytes rejection.</summary>
    protected static NexusSerializationException TrailingBytes(Type type) => new("Trailing bytes after the " + type + " value.");
}

/// <summary>A type serialized by its registered NexNet formatter.</summary>
public sealed class FormatterNative<T> : GoldenNative
{
    /// <inheritdoc />
    public override Type ClrType => typeof(T);

    /// <summary>Decodes one value.</summary>
    public static T? Decode(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
    {
        var reader = new MsgPackReader(data, options);
        T? value = default;
        NexusFormatterRegistry.Get<T>().Deserialize(ref reader, ref value);
        if (!reader.End)
            throw TrailingBytes(typeof(T));
        return value;
    }

    /// <inheritdoc />
    public override byte[] Reencode(ReadOnlyMemory<byte> data, NexusSerializerOptions options)
        => NexusSerializer.Serialize(Decode(data, options));
}
