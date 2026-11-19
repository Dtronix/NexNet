using System.Buffers.Binary;

namespace NexNet.Interop.Remote;

/// <summary>The payload kinds of a control frame (control protocol §3).</summary>
public enum ControlFrameKind : byte
{
    /// <summary>One UTF-8 JSON object.</summary>
    Json = 0x01,

    /// <summary>Reserved for binary payloads; refused in protocol version 1.</summary>
    Binary = 0x02,
}

/// <summary>One control frame.</summary>
/// <param name="Kind">The payload kind.</param>
/// <param name="Payload">The payload bytes.</param>
public readonly record struct ControlFrame(ControlFrameKind Kind, byte[] Payload);

/// <summary>
/// Reads and writes control frames: a 32-bit big-endian length (of the kind byte and the payload), a kind byte, the
/// payload (docs/internals/interop-control-protocol.md §3).
/// </summary>
public static class ControlFraming
{
    /// <summary>The largest frame: 64 MiB after the length field.</summary>
    public const int MaxFrameSize = 64 * 1024 * 1024;

    /// <summary>Writes one frame; the caller serializes writes.</summary>
    /// <exception cref="ControlProtocolException">The frame is larger than <see cref="MaxFrameSize"/>.</exception>
    public static async ValueTask WriteAsync(Stream stream, ControlFrameKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.Length > MaxFrameSize - 1)
            throw new ControlProtocolException($"A control frame of {payload.Length + 1} bytes exceeds the {MaxFrameSize} byte limit.");

        var buffer = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)(payload.Length + 1));
        buffer[4] = (byte)kind;
        payload.CopyTo(buffer.AsMemory(5));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame; null when the stream ends cleanly between frames.</summary>
    /// <exception cref="ControlProtocolException">
    /// A zero or oversized length, an unknown or reserved kind, or the end of the stream inside a frame.
    /// </exception>
    public static async ValueTask<ControlFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[5];
        var read = await ReadAtLeastAsync(stream, header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < 4)
            throw new ControlProtocolException("The control stream ended inside a frame header.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0)
            throw new ControlProtocolException("A control frame has a zero length.");
        if (length > MaxFrameSize)
            throw new ControlProtocolException($"A control frame of {length} bytes exceeds the {MaxFrameSize} byte limit.");

        if (await ReadAtLeastAsync(stream, header.AsMemory(4, 1), cancellationToken).ConfigureAwait(false) < 1)
            throw new ControlProtocolException("The control stream ended inside a frame header.");

        var kind = (ControlFrameKind)header[4];
        if (kind != ControlFrameKind.Json)
        {
            throw new ControlProtocolException(kind == ControlFrameKind.Binary
                ? "Binary control frames (kind 0x02) are reserved in protocol version 1."
                : $"Unknown control frame kind 0x{header[4]:x2}.");
        }

        var payload = new byte[length - 1];
        if (await ReadAtLeastAsync(stream, payload, cancellationToken).ConfigureAwait(false) < payload.Length)
            throw new ControlProtocolException("The control stream ended inside a frame.");
        return new ControlFrame(kind, payload);
    }

    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.Slice(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}
