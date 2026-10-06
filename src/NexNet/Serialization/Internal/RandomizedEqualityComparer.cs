using System;
using System.Collections.Generic;

namespace NexNet.Serialization;

/// <summary>
/// Equality comparer that mixes hash codes with the per-process random seed of <see cref="HashCode"/>,
/// so attacker-chosen keys cannot be crafted to land in the same bucket (hash flooding).
/// </summary>
internal sealed class RandomizedEqualityComparer<T> : IEqualityComparer<T>
{
    public static readonly RandomizedEqualityComparer<T> Instance = new();

    public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x, y);

    public int GetHashCode(T obj)
    {
        // 64-bit primitives fold their halves in GetHashCode (lo ^ hi), which is trivially collidable.
        // Hash both halves separately instead.
        if (typeof(T) == typeof(long))
        {
            var v = (long)(object)obj!;
            return HashCode.Combine((int)v, (int)(v >> 32));
        }

        if (typeof(T) == typeof(ulong))
        {
            var v = (ulong)(object)obj!;
            return HashCode.Combine((uint)v, (uint)(v >> 32));
        }

        return HashCode.Combine(obj is null ? 0 : EqualityComparer<T>.Default.GetHashCode(obj));
    }
}

/// <summary>
/// Chooses comparers for deserialized dictionaries and sets.
/// </summary>
internal static class CollectionComparers
{
    public static IEqualityComparer<T>? Get<T>(NexusSerializerOptions options)
    {
        // Strings already use randomized hashing in .NET once collisions are detected.
        if (!options.HashFloodingResistantCollections || typeof(T) == typeof(string))
            return null;

        return RandomizedEqualityComparer<T>.Instance;
    }
}
