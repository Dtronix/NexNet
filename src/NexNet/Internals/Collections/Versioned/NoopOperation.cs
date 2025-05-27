﻿using System.Collections.Immutable;

namespace NexNet.Internals.Collections.Versioned;

/// <summary>
/// Represents an operation to modify the value of an item at a specified index in the list.
/// </summary>
internal class NoopOperation<T> : Operation<T>
{
    public static readonly NoopOperation<T> Instance = new();
    /// <summary>
    /// Initializes a new instance of the <see cref="NoopOperation{T}"/> class.  Noop will be performed upon apply.
    /// </summary>
    private NoopOperation()
    {
    }

    /// <inheritdoc />
    public override void Apply(ref VersionedList<T>.ListState state)
    {
        // Noop
    }

    /// <inheritdoc />
    public override bool TransformAgainst(Operation<T> other)
    {
        return false;
    }

    /// <inheritdoc />
    public override NoopOperation<T> Clone()
    {
        return new NoopOperation<T>();
    }
}
