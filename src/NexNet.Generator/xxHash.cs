// MIT Licensed.
// https://github.com/TommasoBelluzzo/FastHashes
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace NexNet.Generator;

/// <summary>Represents the xxHash32 implementation. This class cannot be derived.</summary>
internal sealed class XxHash32
{
    private const UInt32 P1 = 0x9E3779B1u;
    private const UInt32 P2 = 0x85EBCA77u;
    private const UInt32 P3 = 0xC2B2AE3Du;
    private const UInt32 P4 = 0x27D4EB2Fu;
    private const UInt32 P5 = 0x165667B1u;

    private readonly UInt32 m_Seed = 226426531;
        

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt32 Add(UInt32 v1, UInt32 v2, UInt32 v3, UInt32 v4)
    {
        v1 = RotateLeft(v1, 1);
        v2 = RotateLeft(v2, 7);
        v3 = RotateLeft(v3, 12);
        v4 = RotateLeft(v4, 18);

        return v1 + v2 + v3 + v4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt32 Mix(UInt32 v, UInt32 p1, UInt32 p2, Int32 r, UInt32 k)
    {
        v += k * p1;
        v = RotateLeft(v, r) * p2;

        return v;
    }

    public uint ComputeHash(ReadOnlySpan<Byte> buffer)
    {
        Int32 offset = 0;
        Int32 count = buffer.Length;

        UInt32 hash = m_Seed;

        if (count == 0)
        {
            hash += P5;
            goto Finalize;
        }

        Int32 blocks = count / 16;

        if (blocks > 0)
        {
            UInt32 v1 = hash + P1 + P2;
            UInt32 v2 = hash + P2;
            UInt32 v3 = hash;
            UInt32 v4 = hash - P1;

            while (blocks-- > 0)
            {
                v1 = Mix(v1, P2, P1, 13, Read32(buffer, offset));
                offset += 4;
                v2 = Mix(v2, P2, P1, 13, Read32(buffer, offset));
                offset += 4;
                v3 = Mix(v3, P2, P1, 13, Read32(buffer, offset));
                offset += 4;
                v4 = Mix(v4, P2, P1, 13, Read32(buffer, offset));
                offset += 4;
            }

            hash = Add(v1, v2, v3, v4);
        }
        else
            hash += P5;

        hash += (UInt32)count;

        Int32 remainder = count & 15;

        if (remainder > 0)
        {
            blocks = remainder / 4;
            remainder &= 3;

            while (blocks-- > 0)
            {
                hash = Mix(hash, P3, P4, 17, Read32(buffer, offset));
                offset += 4;
            }
        }

        for (Int32 i = 0; i < remainder; ++i)
            hash = Mix(hash, P5, P1, 11, buffer[offset + i]);

        Finalize:

        hash ^= hash >> 15;
        hash *= P2;
        hash ^= hash >> 13;
        hash *= P3;
        hash ^= hash >> 16;

        return hash;
    }


    /// <summary>Reads a 4-bytes unsigned integer from the specified byte span.</summary>
    /// <param name="buffer">The <see cref="T:System.ReadOnlySpan`1{T}">ReadOnlySpan&lt;byte&gt;</see> to read.</param>
    /// <param name="offset">The buffer start offset.</param>
    /// <returns>An <see cref="T:System.UInt32"/> value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt32 Read32(ReadOnlySpan<Byte> buffer, Int32 offset)
    {
        ReadOnlySpan<Byte> slice = buffer.Slice(offset, 4);
        UInt32 v = BinaryPrimitives.ReadUInt32LittleEndian(slice);

        return v;
    }

    /// <summary>Rotates a 4-bytes unsigned integer left by the specified number of bits.</summary>
    /// <param name="value">The <see cref="T:System.UInt32"/> to rotate.</param>
    /// <param name="rotation">The number of bits to rotate.</param>
    /// <returns>An <see cref="T:System.UInt32"/> value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt32 RotateLeft(UInt32 value, Int32 rotation)
    {
        rotation &= 0x1F;
        return (value << rotation) | (value >> (32 - rotation));
    }
}
