using System;

namespace NexNet.Serialization;

/// <summary>
/// Thrown when MessagePack data cannot be serialized or deserialized.
/// Messages never include payload bytes so untrusted data does not leak into logs.
/// </summary>
public sealed class NexusSerializationException : Exception
{
    /// <summary>
    /// Creates a new serialization exception.
    /// </summary>
    public NexusSerializationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new serialization exception with an inner exception.
    /// </summary>
    public NexusSerializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal static NexusSerializationException UnexpectedCode(byte code, string expected)
        => new($"Unexpected MessagePack code 0x{code:x2} while reading {expected}.");

    internal static NexusSerializationException Truncated()
        => new("Unexpected end of MessagePack data.");

    internal static NexusSerializationException InvalidMessage(string name)
        => new($"Invalid {name} message body.");

    internal static NexusSerializationException TrailingBytes(string name)
        => new($"Trailing bytes after {name} message body.");

    internal static NexusSerializationException MissingFormatter(Type type)
        => new($"No NexNet formatter is registered for {type}. Annotate it with [NexusObject], register a formatter with [assembly: NexusFormatter<TFormatter, T>], or declare it with [assembly: NexusSerializable<T>].");

    internal static NexusSerializationException DepthExceeded(int maxDepth)
        => new($"MessagePack data exceeds the maximum depth of {maxDepth}.");

    internal static NexusSerializationException LengthExceedsRemaining(long length, long remaining)
        => new($"MessagePack length {length} exceeds the {remaining} remaining bytes.");

    internal static NexusSerializationException InvalidUtf8(Exception inner)
        => new("MessagePack string contains invalid UTF-8.", inner);

    internal static NexusSerializationException Overflow(string target, Exception inner)
        => new($"MessagePack integer does not fit in {target}.", inner);

    /// <summary>
    /// Union tag not known to the reader.
    /// </summary>
    public static NexusSerializationException UnknownUnionTag(int tag, Type unionType)
        => new($"Unknown union tag {tag} for {unionType}.");

    /// <summary>
    /// Runtime type not covered by any union case.
    /// </summary>
    public static NexusSerializationException UnknownUnionType(Type runtimeType, Type unionType)
        => new($"Type {runtimeType} is not a registered union case of {unionType}.");

    /// <summary>
    /// The argument array of an invocation does not have the expected element count.
    /// </summary>
    public static NexusSerializationException ArgumentCountMismatch(int methodId, int expected)
        => new($"Invocation of method {methodId} expected {expected} arguments.");

    /// <summary>
    /// Array header count did not match an expected fixed count.
    /// </summary>
    public static NexusSerializationException UnexpectedCount(int expected, int actual)
        => new($"Expected a MessagePack array of {expected} elements but found {actual}.");

    internal static NexusSerializationException InvalidExt(string reason)
        => new($"Invalid NexNet ext value: {reason}.");
}
