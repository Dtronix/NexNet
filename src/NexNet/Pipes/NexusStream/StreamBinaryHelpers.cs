using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Helper methods for binary serialization of NexStream protocol data.
/// All multi-byte integers use little-endian byte order.
/// </summary>
internal static class StreamBinaryHelpers
{
    /// <summary>
    /// Maximum allowed length for resource ID strings.
    /// </summary>
    public const int MaxResourceIdLength = 2000;

    /// <summary>
    /// Length value indicating a null string.
    /// </summary>
    public const ushort NullStringLength = 0xFFFF;

    // =============================================
    // Integer Writing (Little-Endian)
    // =============================================

    /// <summary>
    /// Writes a 16-bit unsigned integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt16(Span<byte> buffer, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
    }

    /// <summary>
    /// Writes a 32-bit signed integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt32(Span<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
    }

    /// <summary>
    /// Writes a 32-bit unsigned integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt32(Span<byte> buffer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
    }

    /// <summary>
    /// Writes a 64-bit signed integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInt64(Span<byte> buffer, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
    }

    /// <summary>
    /// Writes a 64-bit unsigned integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt64(Span<byte> buffer, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
    }

    /// <summary>
    /// Writes a double-precision floating point value in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDouble(Span<byte> buffer, double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
    }

    // =============================================
    // Integer Reading (Little-Endian)
    // =============================================

    /// <summary>
    /// Reads a 16-bit unsigned integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort ReadUInt16(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    /// <summary>
    /// Reads a 32-bit signed integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadInt32(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    /// <summary>
    /// Reads a 64-bit signed integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadInt64(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    /// <summary>
    /// Reads a 64-bit unsigned integer in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    /// <summary>
    /// Reads a double-precision floating point value in little-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ReadDouble(ReadOnlySpan<byte> buffer)
    {
        return BinaryPrimitives.ReadDoubleLittleEndian(buffer);
    }

    // =============================================
    // String Serialization
    // =============================================

    /// <summary>
    /// Writes a string to the buffer with a 2-byte length prefix.
    /// Null strings are encoded as length 0xFFFF.
    /// </summary>
    /// <param name="buffer">The buffer to write to.</param>
    /// <param name="value">The string value, or null.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteString(Span<byte> buffer, string? value)
    {
        if (value == null)
        {
            WriteUInt16(buffer, NullStringLength);
            return 2;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue - 1) // Reserve 0xFFFF for null
        {
            throw new ArgumentException($"String is too long ({byteCount} bytes). Maximum is {ushort.MaxValue - 1} bytes.", nameof(value));
        }

        WriteUInt16(buffer, (ushort)byteCount);
        Encoding.UTF8.GetBytes(value, buffer.Slice(2));
        return 2 + byteCount;
    }

    /// <summary>
    /// Reads a string from the buffer with a 2-byte length prefix.
    /// Length 0xFFFF indicates a null string.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <param name="bytesRead">The number of bytes consumed.</param>
    /// <returns>The string value, or null.</returns>
    public static string? ReadString(ReadOnlySpan<byte> buffer, out int bytesRead)
    {
        var length = ReadUInt16(buffer);

        if (length == NullStringLength)
        {
            bytesRead = 2;
            return null;
        }

        bytesRead = 2 + length;
        return Encoding.UTF8.GetString(buffer.Slice(2, length));
    }

    /// <summary>
    /// Gets the encoded size of a string including the 2-byte length prefix.
    /// </summary>
    /// <param name="value">The string value, or null.</param>
    /// <returns>The number of bytes required to encode the string.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetStringSize(string? value)
    {
        if (value == null)
            return 2;

        return 2 + Encoding.UTF8.GetByteCount(value);
    }

    // =============================================
    // Boolean Serialization
    // =============================================

    /// <summary>
    /// Writes a boolean value as a single byte (0 or 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteBool(Span<byte> buffer, bool value)
    {
        buffer[0] = value ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Reads a boolean value from a single byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadBool(ReadOnlySpan<byte> buffer)
    {
        return buffer[0] != 0;
    }
}
