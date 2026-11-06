using System;
using System.Buffers;
using NexNet.Serialization;

namespace NexNet.Messages;

/// <summary>
/// The detail of a failed invocation: the optional third element of an Exception <see cref="InvocationResultMessage"/>,
/// <c>[code (int32|nil), type (str|nil), message (str|nil)]</c>.
/// </summary>
internal static class RemoteErrorDetail
{
    /// <summary>
    /// Longest exception type name sent or accepted, in UTF-16 chars.
    /// </summary>
    public const int MaxTypeLength = 256;

    /// <summary>
    /// Longest message sent or accepted, in UTF-16 chars.
    /// </summary>
    public const int MaxMessageLength = 2048;

    /// <summary>
    /// Writes the detail, truncating the type and message to their limits.
    /// </summary>
    public static void Write(ref MsgPackWriter writer, int? code, string? type, string? message)
    {
        writer.WriteArrayHeader(3);
        if (code is { } value)
            writer.Write(value);
        else
            writer.WriteNil();
        writer.Write(Truncate(type, MaxTypeLength));
        writer.Write(Truncate(message, MaxMessageLength));
    }

    /// <summary>
    /// Reads a detail. Returns false, and no partial detail, for anything that is not exactly a valid one; never
    /// throws.
    /// </summary>
    public static bool TryRead(in ReadOnlySequence<byte> value, NexusSerializerOptions options,
        out int? code, out string? type, out string? message)
    {
        code = null;
        type = null;
        message = null;
        try
        {
            var reader = new MsgPackReader(value, options);
            if (reader.ReadArrayHeader() != 3)
                return false;

            int? readCode = reader.TryReadNil() ? null : reader.ReadInt32();
            var readType = reader.ReadString();
            var readMessage = reader.ReadString();

            if (!reader.End
                || readType?.Length > MaxTypeLength
                || readMessage?.Length > MaxMessageLength)
                return false;

            code = readCode;
            type = readType;
            message = readMessage;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Cuts <paramref name="value"/> to at most <paramref name="max"/> chars without splitting a surrogate pair.
    /// </summary>
    private static string? Truncate(string? value, int max)
    {
        if (value == null || value.Length <= max)
            return value;

        var length = max;
        if (char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value.Substring(0, length);
    }
}
