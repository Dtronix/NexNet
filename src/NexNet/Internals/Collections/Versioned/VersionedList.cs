using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using NexNet.Internals.Collections.Lists;
using NexNet.Logging;

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
    private INexusLogger? _logger;
    
    public record ListState(ImmutableList<T> List, int Version);

    /// <summary>
    /// Current version of the list.
    /// </summary>
    public int Version => State.Version;
    
    /// <summary>
    /// Max number of versions that this list can store.
    /// </summary>
    public int MaxVersions { get; }

    /// <summary>
    /// Oldest base version a request can have and still be rebased exactly against the stored history.
    /// Requests based on an older version are still accepted when the oldest stored entry is a Clear.
    /// </summary>
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
    /// <param name="logger"></param>
    public VersionedList(int maxVersions = 256, INexusLogger? logger = null)
    {
        MaxVersions = maxVersions;
        State = new ListState(ImmutableList<T>.Empty, 0);
        _history = new IndexedCircularList<Operation<T>>(maxVersions);
        _logger = logger;
    }
    
    /// <summary>
    /// Process a client operation generated at clientVersion.
    /// Rebase, apply, record, and assign new version.
    /// </summary>
    /// <remarks>
    /// The history is indexed by version: the entry at global index <c>v</c> is the operation that moved the list
    /// from version <c>v</c> to <c>v + 1</c>. A request based on version <c>b</c> is transformed against every entry
    /// in <c>[b, Version)</c>.
    /// <para>
    /// A Clear wipes the history and becomes its first entry. A request whose base predates that Clear is rebased
    /// starting at the Clear: every operation before the Clear is irrelevant because the Clear removes its effects.
    /// </para>
    /// <para>
    /// A request whose base version has fallen out of the history (more than <see cref="MaxVersions"/> changes
    /// since its base, with no Clear in between that still is in the history) cannot be rebased. It is discarded:
    /// the method returns <see cref="NoopOperation{T}"/> with <see cref="ListProcessResult.OutOfOperationalRange"/>,
    /// so the server answers the requester with Noop instead of closing its pipe.
    /// </para>
    /// <para>
    /// A Clear request is applied for any base version up to the current version; it has nothing to rebase.
    /// </para>
    /// </remarks>
    /// <param name="op">The operation to process.</param>
    /// <param name="baseVersion">The client version on which the operation is based.</param>
    /// <param name="result">The result of the process.</param>
    /// <returns>
    /// The rebased operation after applying.
    /// <see cref="NoopOperation{T}"/> indicates that the passed operation was made irrelevant invalid and should be discarded. Normally due to the operating item being removed,
    /// or because its base version is older than the stored history (<see cref="ListProcessResult.OutOfOperationalRange"/>).
    /// Returns null if the operation provided is invalid for the provided baseVersion.  Normally means the client has provided bad data.</returns>
    public Operation<T>? ProcessOperation(Operation<T> op, int baseVersion, out ListProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentOutOfRangeException.ThrowIfNegative(baseVersion);

        if (op is NoopOperation<T>)
        {
            result = ListProcessResult.DiscardOperation;
            return op;
        }

        var version = State.Version;
        if (baseVersion > version)
        {
            result = ListProcessResult.InvalidVersion;
            return null;
        }

        // Shortcut other logic as this is the only operation that is possible with a clear.
        // A clear does not depend on its base version, so it never needs rebasing.
        if (op is ClearOperation<T> cleOp)
        {
            _history.Clear();
            op.Apply(ref State);
            _history.Add(op.Clone());
            result = ListProcessResult.Successful;
            return cleOp;
        }

        // Entries [FirstIndex, Version) are stored. FirstIndex == Version when the history is empty.
        long rebaseStart = baseVersion;
        if (baseVersion < _history.FirstIndex)
        {
            // The entries between the base and the history start are gone. This is only recoverable when
            // the oldest stored entry is a Clear: it discards every change before it, so the request is
            // rebased from the Clear onwards.
            if (_history.Count > 0 && _history[_history.FirstIndex] is ClearOperation<T>)
            {
                rebaseStart = _history.FirstIndex;
            }
            else
            {
                _logger?.LogDebug($"Discarding operation based on version {baseVersion}; oldest rebasable version is {_history.FirstIndex}.");
                result = ListProcessResult.OutOfOperationalRange;
                return NoopOperation<T>.Instance;
            }
        }

        for (var i = rebaseStart; i < version; i++)
        {
            if (!op.TransformAgainst(_history[i]))
            {
                result = ListProcessResult.DiscardOperation;
                return NoopOperation<T>.Instance;
            }
        }

        if (!ValidateOperation(op))
        {
            result = ListProcessResult.BadOperation;
            return null;
        }

        op.Apply(ref State);
        _history.Add(op.Clone());

        result = ListProcessResult.Successful;
        return op;
    }


    public ListProcessResult ApplyOperation(Operation<T> op, int version)
    {
        if (!ValidateOperation(op))
            return ListProcessResult.BadOperation;

        op.Apply(ref State, version);

        return ListProcessResult.Successful;
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
    
    public override int GetHashCode()
    {
        return RuntimeHelpers.GetHashCode(this);
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
        // Keep the history indexed by version: the next entry moves the list from version 0.
        _history.Reset(0);
    }

    IEnumerator IEnumerable.GetEnumerator() => State.List.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => State.List.GetEnumerator();


    public int IndexOf(T item) => State.List.IndexOf(item);
    public int IndexOf(T item, out int version)
    {
        var state = State;
        version = state.Version;
        return state.List.IndexOf(item);
    }

    public bool Contains(T item) => State.List.Contains(item);

    public void CopyTo(T[] array, int arrayIndex)
    {
        State.List.CopyTo(array, arrayIndex);
    }

    public T this[int index] => State.List[index];
    
    private bool ValidateOperation(Operation<T> op)
    {
        var itemCount = State.List.Count;

        //_logger?.LogDebug($"Validating operation: {op.GetType().Name}, List count: {itemCount}, Version: {State.Version}");

        switch (op)
        {
            case InsertOperation<T> insOp when insOp.Index < 0 || insOp.Index > itemCount:
                _logger?.LogDebug($"Invalid InsertOperation: Index {insOp.Index} is out of bounds (0-{itemCount})");
                return false;
            case RemoveOperation<T> rmOp when rmOp.Index < 0 || rmOp.Index >= itemCount:
                _logger?.LogDebug($"Invalid RemoveOperation: Index {rmOp.Index} is out of bounds (0-{itemCount - 1})");
                return false;
            case ModifyOperation<T> modOp when modOp.Index < 0 || modOp.Index >= itemCount:
                _logger?.LogDebug($"Invalid ModifyOperation: Index {modOp.Index} is out of bounds (0-{itemCount - 1})");
                return false;
            case MoveOperation<T> mvOp when
                mvOp.FromIndex < 0 ||
                mvOp.FromIndex >= itemCount ||
                mvOp.ToIndex < 0 ||
                mvOp.ToIndex >= itemCount ||
                mvOp.FromIndex == mvOp.ToIndex:
                _logger?.LogDebug($"Invalid MoveOperation: FromIndex {mvOp.FromIndex}, ToIndex {mvOp.ToIndex}, bounds (0-{itemCount - 1})");
                return false;
        }
        
        //_logger?.LogDebug($"Operation validation successful: {op.GetType().Name}");
        return true;
    }

}
