using System.Collections.Generic;

namespace NexNet.Internals.Collections.Lists;

/// <summary>
/// Represents an abstract operation on a list of items, supporting
/// application to a target list, transformation against concurrent operations,
/// index transformation for moves, and cloning.
/// </summary>
internal abstract class Operation<T>
{
    /// <summary>
    /// Apply this operation to the target list.
    /// </summary>
    public abstract void Apply(List<T> list);

    /// <summary>
    /// Transform (rebase) this operation against another concurrent operation.
    /// </summary>
    /// <param name="other">The other operation to transform against.</param>
    /// <returns>
    /// True if the transform was applied.  False if the operation has been made invalid and canceled.
    /// This will happen when a previous operation has removed the value.
    /// </returns>
    public abstract bool TransformAgainst(Operation<T> other);
    
    /// <summary>
    /// Transforms an index based on an element move from one position to another.
    /// </summary>
    /// <param name="index">The original index to transform.</param>
    /// <param name="fromIndex">The source index of the moved element.</param>
    /// <param name="toIndex">The destination index of the moved element.</param>
    /// <returns>The transformed index after accounting for the move.</returns>
    protected static int TransformIndex(int index, int fromIndex, int toIndex)
    {
        // if this index _was_ pointing at the moved element, reroute to its new home
        if (index == fromIndex)
            return toIndex;

        // moving rightwards
        if (fromIndex < toIndex)
        {
            // slots between (fromIndex, toIndex] collapse left by one
            if (index > fromIndex && index <= toIndex)
                return index - 1;
        }
        // moving leftwards
        else if (fromIndex > toIndex)
        {
            // slots in [toIndex, fromIndex) all shift right by one
            if (index >= toIndex && index < fromIndex)
                return index + 1;
        }

        return index;
    }
    
    /// <summary>
    /// Creates a deep copy of this operation.
    /// </summary>
    /// <returns>A new Operation&lt;T&gt; that is a clone of this instance.</returns>
    public abstract Operation<T> Clone();
}
