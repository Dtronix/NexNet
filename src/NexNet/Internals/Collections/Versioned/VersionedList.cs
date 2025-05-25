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
internal class VersionedList<T> : IEquatable<T[]>, IEnumerable<T>
{
    internal ImmutableList<T> List;
    private readonly IndexedCircularList<Operation<T>> _history;
    private int _version = 0;

    /// <summary>
    /// Current version of the list.
    /// </summary>
    public int Version => _version;
    
    /// <summary>
    /// Max number of versions that this list can store.
    /// </summary>
    public int MaxVersions { get; }

    public int MinValidVersion => Math.Max(0, _version - MaxVersions);

    /// <summary>
    /// Get a snapshot of the list.
    /// </summary>
    /// <returns>An array containing the current list items.</returns>
    public IReadOnlyList<T> Items => List;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionedList{T}"/> class with a specified initial capacity.
    /// </summary>
    /// <param name="maxVersions">The initial number of elements that the list can contain.</param>
    public VersionedList(int maxVersions = 256)
    {
        MaxVersions = maxVersions;
        List = ImmutableList<T>.Empty;
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

        if (baseVersion > _version)
        {
            result = ListProcessResult.InvalidVersion;
            return null;
        }

        for (var i = baseVersion; i < _history.Count; i++)
        {
            if (!txOp.TransformAgainst(_history[i]))
            {
                result = ListProcessResult.DiscardOperation;
                return new NoopOperation<T>();
            }
        }
        
        // Invalid operation checks.
        switch (txOp)
        {
            case InsertOperation<T> insOp when insOp.Index < 0 || insOp.Index > List.Count:
            case RemoveOperation<T> rmOp when rmOp.Index < 0 || rmOp.Index >= List.Count:
            case ModifyOperation<T> modOp when modOp.Index < 0 || modOp.Index >= List.Count:
            case MoveOperation<T> mvOp when
                mvOp.FromIndex < 0 ||
                mvOp.FromIndex >= List.Count ||
                mvOp.ToIndex < 0 ||
                mvOp.ToIndex >= List.Count ||
                mvOp.FromIndex == mvOp.ToIndex:
            {
                result = ListProcessResult.BadOperation;
                return null;
            }
        }

        txOp.Apply(ref List);
        _history.Add(txOp);
        _version++;
        
        result = ListProcessResult.Successful;
        return txOp;
    }

    /// <inheritdoc />
    public bool Equals(T[]? other)
    {
        if (other is null) return false;
        if (List.Count != other.Length)
            return false;

        for (var i = 0; i < List.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(List[i], other[i]))
                return false;
        
        return true;
    }

    public IEnumerator<T> GetEnumerator()
    {
        return List.GetEnumerator();
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
        if (List.Count == 0)
            return "[]";

        // Estimate a bit of capacity (optional):
        var sb = new StringBuilder(List.Count * 16);
        
        using (var e = List.GetEnumerator())
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

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
