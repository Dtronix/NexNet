using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using NexNet.Internals.Collections.Lists;

namespace NexNet.Internals.Collections.Versioned;

/// <summary>
/// Maintains a list of items with versioning and an operation history,
/// allowing operational transforms to be processed against client operations
/// for synchronization and conflict resolution.
/// </summary>
internal class VersionedList<T> : IEquatable<T[]>, IReadOnlyList<T>
{
    internal ListState State;
    private readonly IndexedCircularList<Operation<T>> _history;
    
    public record ListState(ImmutableList<T> List, int Version);

    /// <summary>
    /// Current version of the list.
    /// </summary>
    public int Version => State.Version;
    
    /// <summary>
    /// Max number of versions that this list can store.
    /// </summary>
    public int MaxVersions { get; }

    public long MinValidVersion => _history.FirstIndex;

    /// <summary>
    /// Get a snapshot of the list.
    /// </summary>
    /// <returns>An array containing the current list items.</returns>
    public ListState CurrentState => State;

    public int Count => State.List.Count;
    public bool IsReadOnly { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionedList{T}"/> class with a specified initial capacity.
    /// </summary>
    /// <param name="maxVersions">The initial number of elements that the list can contain.</param>
    public VersionedList(int maxVersions = 256)
    {
        MaxVersions = maxVersions;
        State = new ListState(ImmutableList<T>.Empty, 0);
        _history = new IndexedCircularList<Operation<T>>(maxVersions);
    }   

    /// <summary>
    /// Process a client operation generated at clientVersion.
    /// Rebase, apply, record, and assign new version.
    /// </summary>
    /// <param name="txOp">The operation to process.</param>
    /// <param name="baseVersion">The client version on which the operation is based.</param>
    /// <param name="result">The result of the process.</param>
    /// <returns>
    /// The rebased operation after applying.
    /// <see cref="NoopOperation{T}"/> indicates that the passed operation was made irrelevant invalid and should be discarded. Normally due to the operating item being removed.
    /// Returns null if the operation provided is invalid for the provided baseVersion.  Normally means the client has provided bad data.</returns>
    public Operation<T>? ProcessOperation(Operation<T> txOp, int baseVersion, out ListProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(txOp);
        ArgumentOutOfRangeException.ThrowIfNegative(baseVersion);

        if (txOp is NoopOperation<T>)
        {
            result = ListProcessResult.DiscardOperation;
            return txOp;
        }

        if (!_history.ValidateIndex(baseVersion-1) && baseVersion > 0)
        {
            result = ListProcessResult.OutOfOperationalRange;
            return null;
        }

        if (baseVersion > State.Version)
        {
            result = ListProcessResult.InvalidVersion;
            return null;
        }
        
        switch (txOp)
        {
            case ClearOperation<T> cleOp:
                _history.Clear();
                txOp.Apply(ref State);
                _history.Add(txOp);
                result = ListProcessResult.Successful;
                return cleOp;
        }

        for (var i = baseVersion; i < _history.Count; i++)
        {
            if (!txOp.TransformAgainst(_history[i]))
            {
                result = ListProcessResult.DiscardOperation;
                return NoopOperation<T>.Instance;
            }
        }

        var itemCount = State.List.Count;
        
        // Invalid operation checks.
        switch (txOp)
        {
            case InsertOperation<T> insOp when insOp.Index < 0 || insOp.Index > itemCount:
            case RemoveOperation<T> rmOp when rmOp.Index < 0 || rmOp.Index >= itemCount:
            case ModifyOperation<T> modOp when modOp.Index < 0 || modOp.Index >= itemCount:
            case MoveOperation<T> mvOp when
                mvOp.FromIndex < 0 ||
                mvOp.FromIndex >= itemCount ||
                mvOp.ToIndex < 0 ||
                mvOp.ToIndex >= itemCount ||
                mvOp.FromIndex == mvOp.ToIndex:
            {
                result = ListProcessResult.BadOperation;
                return null;
            }
        }

        txOp.Apply(ref State);
        _history.Add(txOp);
        
        result = ListProcessResult.Successful;
        return txOp;
    }

    public void ResetTo(ImmutableList<T> list, int version)
    {
        State = new ListState(list, version);
        _history.Reset(version);
    }

    /// <inheritdoc />
    public bool Equals(T[]? other)
    {
        if (other is null) return false;
        if (State.List.Count != other.Length)
            return false;

        for (var i = 0; i < State.List.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(State.List[i], other[i]))
                return false;
        
        return true;
    }
    
    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != GetType()) return false;
        return Equals((VersionedList<T>)obj);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (State.List.Count == 0)
            return "[]";

        // Estimate a bit of capacity (optional):
        var sb = new StringBuilder(State.List.Count * 16);
        
        using (var e = State.List.GetEnumerator())
        {
            // Append the first item without prefix
            e.MoveNext();
            sb.Append(e.Current);

            // Append remaining items with a ", " prefix
            while (e.MoveNext())
            {
                sb.Append(", ").Append(e.Current);
            }
        }

        return sb.ToString();
    }
    
    public void Reset()
    {
        State = new ListState(ImmutableList<T>.Empty, 0);
        _history.Clear();
    }

    IEnumerator IEnumerable.GetEnumerator() => State.List.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => State.List.GetEnumerator();


    public int IndexOf(T item) => State.List.IndexOf(item);

    public bool Contains(T item) => State.List.Contains(item);

    public void CopyTo(T[] array, int arrayIndex)
    {
        State.List.CopyTo(array, arrayIndex);
    }

    public T this[int index] => State.List[index];
}
