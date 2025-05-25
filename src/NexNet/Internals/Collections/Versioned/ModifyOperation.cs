using System;
using System.Collections.Generic;
using System.Collections.Immutable;

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
    public readonly T NewValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ModifyOperation{T}"/> class.
    /// </summary>
    /// <param name="index">The position of the item to modify.</param>
    /// <param name="newValue">The new value to set at the specified index.</param>
    public ModifyOperation(int index, T newValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        NewValue = newValue;
    }

    /// <inheritdoc />
    public override void Apply(ref ImmutableList<T> list)
    {
        ImmutableInterlocked.Update(ref list, static (list, state) => 
            list.SetItem(state.Index, state.NewValue), (Index, NewValue));
    }

    /// <inheritdoc />
    public override bool TransformAgainst(Operation<T> other)
    {
        switch (other)
        {
            case InsertOperation<T> addOp:
                if (addOp.Index <= Index)
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
        }

        throw new InvalidOperationException("Unknown operation");
    }

    /// <inheritdoc />
    public override ModifyOperation<T> Clone()
    {
        return new ModifyOperation<T>(Index, NewValue);
    }

    /// <inheritdoc />
    public bool Equals(ModifyOperation<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Index == other.Index && EqualityComparer<T>.Default.Equals(NewValue, other.NewValue);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((ModifyOperation<T>)obj);
    }

}
