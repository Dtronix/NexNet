using System.Buffers.Binary;

namespace NexNet.Inspector.Core.Format;

/// <summary>Layout constants of the <c>.nntl</c> format (see <c>docs/internals/nntl-format.md</c>).</summary>
public static class NntlFormat
{
    /// <summary>Format major version this build writes and the highest it reads.</summary>
    public const int Version = 1;

    /// <summary>Bytes before each block body: sync, kind, reserved, length, checksum.</summary>
    public const int BlockHeaderSize = 16;

    /// <summary>Bytes of the trailer at the very end of a closed file.</summary>
    public const int TrailerSize = 16;

    /// <summary>Largest block body the writer emits and the reader accepts.</summary>
    /// <remarks>Bounds what a garbled length field can make the reader allocate.</remarks>
    public const int MaxBodyLength = 64 * 1024 * 1024;

    /// <summary>The string that opens the header block body.</summary>
    public const string Magic = "NNTL";

    /// <summary>Sync marker that opens every block. 0xC1 is the one byte MessagePack never uses, so a body is
    /// unlikely to contain the marker outside of raw binary payloads; the checksum rejects those anyway.</summary>
    public static ReadOnlySpan<byte> SyncMarker => [0xC1, (byte)'N', (byte)'T', (byte)'L'];

    /// <summary>Marker that closes the trailer.</summary>
    public static ReadOnlySpan<byte> TrailerMarker => [(byte)'L', (byte)'T', (byte)'N', 0xC1];

    internal const int IndexEntrySize = 16;

    /// <summary>Fills in a block header in front of an already written body and returns the checksum.</summary>
    internal static void WriteBlockHeader(Span<byte> block, NntlBlockKind kind)
    {
        var bodyLength = block.Length - BlockHeaderSize;
        SyncMarker.CopyTo(block);
        block[4] = (byte)kind;
        block[5] = 0;
        block[6] = 0;
        block[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(8), (uint)bodyLength);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(12), ComputeChecksum(block.Slice(4, 8), block.Slice(BlockHeaderSize)));
    }

    /// <summary>Checksum over header bytes 4..11 (kind, reserved, length) followed by the body.</summary>
    internal static uint ComputeChecksum(ReadOnlySpan<byte> headerFields, ReadOnlySpan<byte> body) =>
        ~Crc32C.Update(Crc32C.Update(Crc32C.Seed, headerFields), body);

    /// <summary>Checks the fixed parts of a block header and returns the body length, or -1.</summary>
    internal static int ParseBlockHeader(ReadOnlySpan<byte> header, long bytesAfterHeader)
    {
        if (!header.StartsWith(SyncMarker) || header[5] != 0 || header[6] != 0 || header[7] != 0)
            return -1;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8));
        if (length > MaxBodyLength || length > bytesAfterHeader)
            return -1;

        return (int)length;
    }

    internal static void WriteTrailer(Span<byte> trailer, long indexOffset)
    {
        BinaryPrimitives.WriteInt64LittleEndian(trailer, indexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer.Slice(8), Crc32C.Compute(trailer.Slice(0, 8)));
        TrailerMarker.CopyTo(trailer.Slice(12));
    }

    /// <summary>
    /// Checks the fixed parts of a block header read from a forward-only stream (a live tap stream), where the bytes
    /// that follow are not known yet.
    /// </summary>
    /// <param name="header">The first <see cref="BlockHeaderSize"/> bytes of the block.</param>
    /// <param name="kind">The block kind.</param>
    /// <param name="bodyLength">Length of the body that follows the header.</param>
    /// <returns>False when the bytes are not a block header.</returns>
    public static bool TryParseBlockHeader(ReadOnlySpan<byte> header, out NntlBlockKind kind, out int bodyLength)
    {
        kind = default;
        bodyLength = header.Length < BlockHeaderSize ? -1 : ParseBlockHeader(header, MaxBodyLength);
        if (bodyLength < 0)
            return false;

        kind = (NntlBlockKind)header[4];
        return true;
    }

    /// <summary>True when the checksum in <paramref name="header"/> matches the header fields and <paramref name="body"/>.</summary>
    /// <param name="header">The block header (<see cref="BlockHeaderSize"/> bytes).</param>
    /// <param name="body">The block body.</param>
    public static bool IsChecksumValid(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body) =>
        header.Length >= BlockHeaderSize
        && BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12)) == ComputeChecksum(header.Slice(4, 8), body);

    /// <summary>Reads the index offset from a trailer.</summary>
    /// <param name="trailer">The last <see cref="TrailerSize"/> bytes of a file or stream.</param>
    /// <param name="indexOffset">Offset of the index block the trailer points to.</param>
    /// <returns>False when the bytes are not a valid trailer.</returns>
    public static bool TryParseTrailerOffset(ReadOnlySpan<byte> trailer, out long indexOffset)
    {
        indexOffset = ParseTrailer(trailer);
        return indexOffset >= 0;
    }

    /// <summary>Returns the index offset a trailer points to, or -1 when the bytes are not a trailer.</summary>
    internal static long ParseTrailer(ReadOnlySpan<byte> trailer)
    {
        if (trailer.Length != TrailerSize || !trailer.Slice(12).SequenceEqual(TrailerMarker))
            return -1;
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer.Slice(8)) != Crc32C.Compute(trailer.Slice(0, 8)))
            return -1;

        var offset = BinaryPrimitives.ReadInt64LittleEndian(trailer);
        return offset < 0 ? -1 : offset;
    }
}
