using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using NexNet.Cache;

namespace NexNet.Internals.Collections.Versioned;

/// <summary>
/// Represents an operation to clear the list.
/// </summary>
internal class ClearOperation<T> : Operation<T>, IEquatable<ClearOperation<T>>
{
    /// <inheritdoc />
    public override void Apply(ref VersionedList<T>.ListState state, int? version = null)
    {
        ImmutableInterlocked.Update(ref state, static (state, version) => 
            new VersionedList<T>.ListState(ImmutableList<T>.Empty, version ?? (state.Version + 1)), version);
    }

    /// <inheritdoc />
    public override bool TransformAgainst(Operation<T> other)
    {
        return true;
    }

    /// <inheritdoc />
    public override ClearOperation<T> Clone()
    {
        return new ClearOperation<T>();
    }
    
    /// <inheritdoc />
    public bool Equals(ClearOperation<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return true;
    }
    

    public override int GetHashCode()
    {
        return RuntimeHelpers.GetHashCode(this);
    }

    public static ClearOperation<T> Rent() => ObjectCache<ClearOperation<T>>.Rent();
    public override void Return() => ObjectCache<ClearOperation<T>>.Return(this);
}
