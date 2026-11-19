using System.Security.Cryptography;

namespace NexNet.Interop;

/// <summary>
/// The deterministic byte pattern of <see cref="IRemotePipe.WritePatternAsync"/>: byte <c>i</c> of seed <c>s</c> is
/// <c>(i * 31 + s) mod 256</c>, the same as <c>InteropServerNexus.Pattern</c>. Every language host implements it.
/// </summary>
public static class PipePattern
{
    /// <summary>Fills <paramref name="destination"/> with the pattern bytes starting at stream offset <paramref name="offset"/>.</summary>
    public static void Fill(Span<byte> destination, long offset, int seed)
    {
        for (var i = 0; i < destination.Length; i++)
            destination[i] = unchecked((byte)((offset + i) * 31 + seed));
    }

    /// <summary>The first <paramref name="length"/> bytes of the pattern.</summary>
    public static byte[] Create(int length, int seed)
    {
        var data = new byte[length];
        Fill(data, 0, seed);
        return data;
    }

    /// <summary>The digest of the first <paramref name="length"/> bytes of the pattern.</summary>
    public static PipeDigest Digest(long length, int seed)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16 * 1024];
        for (long offset = 0; offset < length; offset += buffer.Length)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            Fill(buffer.AsSpan(0, count), offset, seed);
            hash.AppendData(buffer, 0, count);
        }

        return new PipeDigest(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
