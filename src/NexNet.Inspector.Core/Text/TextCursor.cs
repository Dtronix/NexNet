using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NexNet.Inspector.Core.Text;

/// <summary>
/// Where a rendering stopped, so the next one resumes there without repeating or losing a record: the session, the
/// level, the stream position (frames and gaps consumed, in capture order, and how many records of the event at that
/// position were already written) and the filter's fingerprint. <see cref="Encode"/> gives the opaque base64url text
/// the budget line prints (`--after &lt;cursor&gt;`); <see cref="TryParse"/> reads it back.
/// </summary>
/// <param name="SessionId">The session's id.</param>
/// <param name="Level">The level rendered.</param>
/// <param name="Phase">0 before the header, 1 in the events, 2 after the end line.</param>
/// <param name="Frame">Index into the session's frame order of the next frame (summary level: the next line).</param>
/// <param name="Gap">Index of the next gap block.</param>
/// <param name="Skip">Records of the event at this position already written.</param>
/// <param name="Filter">The filter's <see cref="TextFilter.Fingerprint"/>.</param>
public readonly record struct TextCursor(long SessionId, TextLevel Level, byte Phase, int Frame, int Gap, int Skip, ulong Filter)
{
    private const byte Version = 1;
    private const int Size = 1 + 1 + 1 + 8 + 4 + 4 + 4 + 8;

    /// <summary>The start of a session.</summary>
    /// <param name="sessionId">The session's id.</param>
    /// <param name="level">The level.</param>
    /// <param name="filter">The filter's fingerprint.</param>
    public static TextCursor Start(long sessionId, TextLevel level, ulong filter) => new(sessionId, level, 0, 0, 0, 0, filter);

    /// <summary>The opaque text form (base64url, no padding).</summary>
    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[Size];
        bytes[0] = Version;
        bytes[1] = (byte)Level;
        bytes[2] = Phase;
        BinaryPrimitives.WriteInt64LittleEndian(bytes[3..], SessionId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[11..], Frame);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[15..], Gap);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[19..], Skip);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[23..], Filter);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a cursor's text form.</summary>
    /// <param name="text">The text from a budget line.</param>
    /// <param name="cursor">The cursor.</param>
    /// <returns>False for text that is not a cursor of this version.</returns>
    public static bool TryParse([NotNullWhen(true)] string? text, out TextCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
            return false;

        Span<char> chars = stackalloc char[68];
        var length = 0;
        foreach (var c in text.AsSpan().Trim())
            chars[length++] = c switch { '-' => '+', '_' => '/', _ => c };
        while (length % 4 != 0)
            chars[length++] = '=';

        Span<byte> bytes = stackalloc byte[51];
        if (!Convert.TryFromBase64Chars(chars[..length], bytes, out var written) || written != Size || bytes[0] != Version)
            return false;
        if (bytes[1] > (byte)TextLevel.Bytes || bytes[2] > 2)
            return false;

        var frame = BinaryPrimitives.ReadInt32LittleEndian(bytes[11..]);
        var gap = BinaryPrimitives.ReadInt32LittleEndian(bytes[15..]);
        var skip = BinaryPrimitives.ReadInt32LittleEndian(bytes[19..]);
        if (frame < 0 || gap < 0 || skip < 0)
            return false;

        cursor = new TextCursor(BinaryPrimitives.ReadInt64LittleEndian(bytes[3..]), (TextLevel)bytes[1], bytes[2], frame, gap, skip,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[23..]));
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Encode();
}
