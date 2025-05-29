using System;
using System.Collections.Immutable;

namespace NexNet.Internals.Collections.Versioned;

/// <summary>
/// Represents an operation to remove an item at a specified index from the list.
/// </summary>
internal class RemoveOperation<T> : Operation<T>, IEquatable<RemoveOperation<T>>
{
    
    /// <summary>
    /// Index to remove.
    /// </summary>
    public int Index;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoveOperation{T}"/> class.
    /// </summary>
    /// <param name="index">The position at which to remove the item.</param>
    public RemoveOperation(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
    }

    /// <inheritdoc />
    public override void Apply(ref VersionedList<T>.ListState state, int? version = null)
    {
        ImmutableInterlocked.Update(ref state, static (state, args) => 
                new VersionedList<T>.ListState(state.List.RemoveAt(args.Index), args.version ?? (state.Version + 1)),
            (Index, version));
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
                {
                    Index--;
                    return true;
                }

                if (remOp.Index == Index)
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
    public override RemoveOperation<T> Clone()
    {
        return new RemoveOperation<T>(Index);
    }

    /// <inheritdoc />
    public bool Equals(RemoveOperation<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Index == other.Index;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((RemoveOperation<T>)obj);
    }
}
