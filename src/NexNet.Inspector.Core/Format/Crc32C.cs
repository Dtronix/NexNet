using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace NexNet.Inspector.Core.Format;

/// <summary>CRC-32C (Castagnoli, reflected polynomial 0x82F63B78) used to check every <c>.nntl</c> block.</summary>
/// <remarks>
/// Castagnoli rather than the zip CRC because x64 (SSE4.2) and arm64 compute it in hardware; the table path keeps the
/// result identical where neither is available. Written here because the inspector may not take a hashing package.
/// </remarks>
internal static class Crc32C
{
    private const uint Polynomial = 0x82F63B78u;

    private static readonly uint[] Table = BuildTable();

    /// <summary>The register value a new computation starts from.</summary>
    public const uint Seed = 0xFFFFFFFFu;

    /// <summary>Computes the finished CRC-32C of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(Seed, data);

    /// <summary>Feeds <paramref name="data"/> into a running register; finish with a bitwise complement.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        if (Sse42.X64.IsSupported)
        {
            ulong c = crc;
            while (data.Length >= 8)
            {
                c = Sse42.X64.Crc32(c, MemoryMarshal.Read<ulong>(data));
                data = data.Slice(8);
            }

            crc = (uint)c;
            foreach (var b in data)
                crc = Sse42.Crc32(crc, b);
            return crc;
        }

        if (ArmCrc32.Arm64.IsSupported)
        {
            while (data.Length >= 8)
            {
                crc = ArmCrc32.Arm64.ComputeCrc32C(crc, BinaryPrimitives.ReadUInt64LittleEndian(data));
                data = data.Slice(8);
            }

            foreach (var b in data)
                crc = ArmCrc32.ComputeCrc32C(crc, b);
            return crc;
        }

        return UpdateSoftware(crc, data);
    }

    /// <summary>Table-driven fallback; also lets tests check the hardware paths against it.</summary>
    internal static uint UpdateSoftware(uint crc, ReadOnlySpan<byte> data)
    {
        var table = Table;
        foreach (var b in data)
            crc = table[(byte)(crc ^ b)] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ Polynomial : c >> 1;
            table[i] = c;
        }

        return table;
    }
}
