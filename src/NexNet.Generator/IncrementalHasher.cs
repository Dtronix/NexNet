using System.Runtime.CompilerServices;

namespace NexNet.Generator;

/// <summary>
/// Incremental hasher using the FNV-1a algorithm. A plain struct so it can live in a class field
/// (<see cref="Serialization.ShapeHasher"/>); copies hash independently.
/// </summary>
internal struct IncrementalHasher
{
    private const uint FnvPrime = 16777619;
    private const uint FnvOffsetBasis = 2166136261;

    private uint _hash;

    public IncrementalHasher()
    {
        _hash = FnvOffsetBasis;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int value)
    {
        _hash ^= (uint)value;
        _hash *= FnvPrime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(long value)
    {
        Add((int)value);
        Add((int)(value >> 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(byte value)
    {
        _hash ^= value;
        _hash *= FnvPrime;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(ushort value)
    {
        _hash ^= value;
        _hash *= FnvPrime;
    }

    /// <summary>
    /// Hash string without allocation by processing characters directly.
    /// </summary>
    public void AddString(string value)
    {
        foreach (char c in value)
        {
            _hash ^= c;
            _hash *= FnvPrime;
        }
        // Length distinguishes "ab" from sequential "a" + "b"
        Add(value.Length);
    }

    public readonly int ToHashCode() => (int)_hash;
}
