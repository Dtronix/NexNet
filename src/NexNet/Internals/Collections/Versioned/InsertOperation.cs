using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NexNet.Internals.Collections.Versioned;

/// <summary>
/// Represents an operation to insert an item at a specified index in the list.
/// </summary>
internal class InsertOperation<T> : Operation<T>, IEquatable<InsertOperation<T>>
{
    
    /// <summary>
    /// Index to add the provided item.
    /// </summary>
    public int Index;
    
    /// <summary>
    /// Item that is being inserted at the specified index.
    /// </summary>
    public readonly T Item;

    
    /// <summary>
    /// Initializes a new instance of the <see cref="InsertOperation{T}"/> class.
    /// </summary>
    /// <param name="index">The position at which to insert the item.</param>
    /// <param name="item">The item to insert.</param>
    public InsertOperation(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        Item = item;
    }

    /// <inheritdoc />
    public override void Apply(ref VersionedList<T>.ListState state)
    {
        ImmutableInterlocked.Update(ref state, static (state, args) => 
            new VersionedList<T>.ListState(state.List.Insert(args.Index, args.Item), state.Version + 1),
            (Index, Item));
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
    public override InsertOperation<T> Clone()
    {
        return new InsertOperation<T>(Index, Item);
    }
    
    /// <inheritdoc />
    public bool Equals(InsertOperation<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Index == other.Index && EqualityComparer<T>.Default.Equals(Item, other.Item);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((InsertOperation<T>)obj);
    }
}
