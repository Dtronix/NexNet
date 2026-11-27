using System.Buffers.Binary;

namespace NexNet.Inspector.Core.Schema;

/// <summary>
/// Schema identity: the 64-bit FNV-1a hash of the <c>.nnschema</c> bytes (the generator's <c>InspectorSchemaHash</c>),
/// and its 8-byte form, little-endian, as recordings store it (a session's schema hash, a schema block's hash).
/// </summary>
public static class SchemaHash
{
    /// <summary>Bytes in the stored form.</summary>
    public const int Size = 8;

    private const ulong OffsetBasis = 0xcbf29ce484222325;
    private const ulong Prime = 0x100000001b3;

    /// <summary>Computes the hash of schema bytes.</summary>
    /// <param name="schema">The <c>.nnschema</c> bytes.</param>
    public static ulong Compute(ReadOnlySpan<byte> schema)
    {
        var hash = OffsetBasis;
        foreach (var b in schema)
        {
            hash ^= b;
            hash *= Prime;
        }

        return hash;
    }

    /// <summary>The stored form of a hash.</summary>
    /// <param name="hash">The hash.</param>
    public static byte[] ToBytes(ulong hash)
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, hash);
        return bytes;
    }

    /// <summary>Writes the stored form of a hash.</summary>
    /// <param name="hash">The hash.</param>
    /// <param name="destination">At least <see cref="Size"/> bytes.</param>
    public static void Write(ulong hash, Span<byte> destination) => BinaryPrimitives.WriteUInt64LittleEndian(destination, hash);

    /// <summary>Reads a stored hash.</summary>
    /// <param name="stored">The stored form.</param>
    /// <param name="hash">The hash.</param>
    /// <returns>False when <paramref name="stored"/> is not exactly <see cref="Size"/> bytes (an empty one means no schema).</returns>
    public static bool TryRead(ReadOnlySpan<byte> stored, out ulong hash)
    {
        if (stored.Length != Size)
        {
            hash = 0;
            return false;
        }

        hash = BinaryPrimitives.ReadUInt64LittleEndian(stored);
        return true;
    }

    /// <summary>True when a stored hash names this hash; false for a mismatch or a malformed stored hash.</summary>
    /// <param name="stored">The stored form, from a recording.</param>
    /// <param name="hash">The hash of the schema in hand, for example <see cref="NexusSchema.Hash"/>.</param>
    public static bool Matches(ReadOnlySpan<byte> stored, ulong hash) => TryRead(stored, out var value) && value == hash;

    /// <summary>True when a stored hash names these schema bytes.</summary>
    /// <param name="stored">The stored form, from a recording.</param>
    /// <param name="schema">The <c>.nnschema</c> bytes in hand.</param>
    public static bool Matches(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> schema) => Matches(stored, Compute(schema));
}
