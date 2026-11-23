using System.Security.Cryptography;

namespace NexNet.Inspector.Tap;

/// <summary>The marker that replaces a redacted auth token in tap streams and recordings.</summary>
/// <remarks>
/// The marker has the token's length, so every frame length and offset in the capture stays valid, and it is ASCII so
/// a hex or text view shows it plainly: the lowercase hex digits of SHA-256(token), as many as fit (at most 64), then
/// <c>*</c> for any bytes beyond 64. Two captures of the same token carry the same marker, so a token can be
/// matched across recordings without being stored. The transport op flags the range as redacted.
/// </remarks>
public static class InspectorRedaction
{
    private const int HexDigits = SHA256.HashSizeInBytes * 2;

    /// <summary>Writes the marker for <paramref name="token"/> into <paramref name="marker"/>, which has the token's length.</summary>
    /// <param name="token">The raw token bytes.</param>
    /// <param name="marker">Destination; may be the token's own bytes.</param>
    public static void WriteMarker(ReadOnlySpan<byte> token, Span<byte> marker)
    {
        if (marker.Length != token.Length)
            throw new ArgumentException("The marker must have the token's length.", nameof(marker));

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(token, hash);
        for (var i = 0; i < marker.Length; i++)
        {
            if (i >= HexDigits)
            {
                marker[i] = (byte)'*';
                continue;
            }

            var nibble = (i & 1) == 0 ? hash[i >> 1] >> 4 : hash[i >> 1] & 0xF;
            marker[i] = (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
        }
    }
}
