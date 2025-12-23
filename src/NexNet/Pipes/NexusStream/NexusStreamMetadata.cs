using System;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Metadata describing a stream's capabilities and properties.
/// </summary>
public readonly struct NexusStreamMetadata
{
    /// <summary>
    /// Length of the stream in bytes. -1 if unknown.
    /// </summary>
    public long Length { get; init; }

    /// <summary>
    /// Whether the stream has a known length.
    /// </summary>
    public bool HasKnownLength { get; init; }

    /// <summary>
    /// Whether the stream supports seeking.
    /// </summary>
    public bool CanSeek { get; init; }

    /// <summary>
    /// Whether the stream supports reading.
    /// </summary>
    public bool CanRead { get; init; }

    /// <summary>
    /// Whether the stream supports writing.
    /// </summary>
    public bool CanWrite { get; init; }

    // Flags layout:
    // Bit 0: HasKnownLength
    // Bit 1: CanSeek
    // Bit 2: CanRead
    // Bit 3: CanWrite
    private const byte HasKnownLengthFlag = 0x01;
    private const byte CanSeekFlag = 0x02;
    private const byte CanReadFlag = 0x04;
    private const byte CanWriteFlag = 0x08;

    /// <summary>
    /// Size of the metadata when serialized: Flags (1) + Length (8) = 9 bytes.
    /// </summary>
    public const int Size = 9;

    /// <summary>
    /// Gets the flags byte representing the boolean properties.
    /// </summary>
    internal byte GetFlags()
    {
        byte flags = 0;
        if (HasKnownLength) flags |= HasKnownLengthFlag;
        if (CanSeek) flags |= CanSeekFlag;
        if (CanRead) flags |= CanReadFlag;
        if (CanWrite) flags |= CanWriteFlag;
        return flags;
    }

    /// <summary>
    /// Writes the metadata to a buffer.
    /// </summary>
    /// <param name="buffer">Buffer to write to. Must be at least <see cref="Size"/> bytes.</param>
    public void Write(Span<byte> buffer)
    {
        buffer[0] = GetFlags();
        StreamBinaryHelpers.WriteInt64(buffer.Slice(1), Length);
    }

    /// <summary>
    /// Reads metadata from a buffer.
    /// </summary>
    /// <param name="buffer">Buffer to read from. Must be at least <see cref="Size"/> bytes.</param>
    /// <returns>The deserialized metadata.</returns>
    public static NexusStreamMetadata Read(ReadOnlySpan<byte> buffer)
    {
        var flags = buffer[0];
        var length = StreamBinaryHelpers.ReadInt64(buffer.Slice(1));

        return new NexusStreamMetadata
        {
            HasKnownLength = (flags & HasKnownLengthFlag) != 0,
            CanSeek = (flags & CanSeekFlag) != 0,
            CanRead = (flags & CanReadFlag) != 0,
            CanWrite = (flags & CanWriteFlag) != 0,
            Length = length
        };
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"Metadata[Length={Length}, HasKnownLength={HasKnownLength}, CanSeek={CanSeek}, CanRead={CanRead}, CanWrite={CanWrite}]";
    }
}
