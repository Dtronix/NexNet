using System;
using System.Collections.Immutable;

namespace NexNet.Internals.Collections.Versioned;

/// <summary>
/// Represents an operation to move an item from one index to another within the list.
/// </summary>
internal class MoveOperation<T> : Operation<T>, IEquatable<MoveOperation<T>>
{

    /// <summary>
    /// Source index to move.
    /// </summary>
    public int FromIndex;

    /// <summary>
    /// Destination index for moving the source to.
    /// </summary>
    public int ToIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoveOperation{T}"/> class.
    /// </summary>
    /// <param name="fromIndex">The original index of the item to move.</param>
    /// <param name="toIndex">The target index to move the item to.</param>
    public MoveOperation(int fromIndex, int toIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(toIndex);
        
        FromIndex = fromIndex;
        ToIndex = toIndex;
    }

    /// <inheritdoc />
    public override void Apply(ref VersionedList<T>.ListState state, int? version = null)
    {
        ImmutableInterlocked.Update(ref state, static (state, args) =>
        {
            var currentList = state.List.RemoveAt(args.FromIndex);
            currentList = currentList.Insert(args.ToIndex, state.List[args.FromIndex]);
            return new VersionedList<T>.ListState(currentList, args.version ?? (state.Version + 1));
        }, (FromIndex, ToIndex, version));
    }

    /// <inheritdoc />
    public override bool TransformAgainst(Operation<T> other)
    {
        switch (other)
        {
            case InsertOperation<T> insertOp:
                if (insertOp.Index <= FromIndex)
                    FromIndex++;
                if (insertOp.Index <= ToIndex)
                    ToIndex++;

                return true;

            case RemoveOperation<T> remOp:
                if (remOp.Index < FromIndex)
                    FromIndex--;
                else if (remOp.Index == FromIndex)
                    return false;

                if (remOp.Index < ToIndex)
                    ToIndex--;
                else if (remOp.Index == ToIndex)
                    return false;

                return true;

            case MoveOperation<T> moveOp:
                FromIndex = TransformIndex(FromIndex, moveOp.FromIndex, moveOp.ToIndex);
                ToIndex = TransformIndex(ToIndex, moveOp.FromIndex, moveOp.ToIndex);
                return true;

            case ModifyOperation<T>:
                return true;
            
            case ClearOperation<T> remOp:
                return false;
        }

        throw new InvalidOperationException("Unknown operation");
    }
    
    /// <inheritdoc />
    public override MoveOperation<T> Clone()
    {
        return new MoveOperation<T>(FromIndex, ToIndex);
    }

    /// <inheritdoc />
    public bool Equals(MoveOperation<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return FromIndex == other.FromIndex && ToIndex == other.ToIndex;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((MoveOperation<T>)obj);
    }
}
