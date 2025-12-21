using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using NexNet.Pools;

namespace NexNet.Internals.Collections.Versioned;

/// <summary>
/// Represents an operation to modify the value of an item at a specified index in the list.
/// </summary>
internal class ModifyOperation<T> : Operation<T>, IEquatable<ModifyOperation<T>>
{

    /// <summary>
    /// Index to modify.
    /// </summary>
    public int Index;

    /// <summary>
    /// Value to set.
    /// </summary>
    public T Value = default!;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModifyOperation{T}"/> class.
    /// </summary>
    /// <param name="index">The position of the item to modify.</param>
    /// <param name="value">The new value to set at the specified index.</param>
    public ModifyOperation(int index, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        Value = value;
    }

    public ModifyOperation()
    {
        
    }

    /// <inheritdoc />
    public override void Apply(ref VersionedList<T>.ListState state, int? version = null)
    {
        ImmutableInterlocked.Update(ref state, static (state, args) => 
                new VersionedList<T>.ListState(state.List.SetItem(args.Index, args.Value), args.version ?? (state.Version + 1)),
            (Index, Value, version));
    }

    /// <inheritdoc />
    public override bool TransformAgainst(Operation<T> other)
    {
        switch (other)
        {
            case InsertOperation<T> insertOp:
                if (insertOp.Index <= Index)
                    Index++;
                return true;

            case RemoveOperation<T> remOp:
                if (remOp.Index < Index)
                    Index--;
                else if (remOp.Index == Index)
                    return false;
                return true;

            case MoveOperation<T> moveOp:
                Index = TransformIndex(Index, moveOp.FromIndex, moveOp.ToIndex);
                return true;

            case ModifyOperation<T>:
                return true;
            
            case ClearOperation<T>:
                return false;
        }

        throw new InvalidOperationException("Unknown operation");
    }

    /// <inheritdoc />
    public override ModifyOperation<T> Clone()
    {
        return new ModifyOperation<T>(Index, Value);
    }

    /// <inheritdoc />
    public bool Equals(ModifyOperation<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Index == other.Index && EqualityComparer<T>.Default.Equals(Value, other.Value);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((ModifyOperation<T>)obj);
    }
    
    public override int GetHashCode()
    {
        return RuntimeHelpers.GetHashCode(this);
    }

    
    public static ModifyOperation<T> Rent() => StaticObjectPool<ModifyOperation<T>>.Rent();
    public override void Return() => StaticObjectPool<ModifyOperation<T>>.Return(this);

}
