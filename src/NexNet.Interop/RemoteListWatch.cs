using System.Diagnostics;
using NexNet.Collections;

namespace NexNet.Interop;

/// <summary>
/// The recorder behind every <see cref="IRemoteListWatch{T}"/>: drivers add the changes their client raises, tests
/// read and wait on them.
/// </summary>
internal sealed class RemoteListWatch<T> : IRemoteListWatch<T>
{
    private readonly object _gate = new();
    private readonly List<RemoteListChange<T>> _changes = new();
    private readonly Func<ValueTask> _detach;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    /// <param name="collection">The collection, for messages.</param>
    /// <param name="detach">Stops the client's events reaching this watch; runs once, on dispose.</param>
    public RemoteListWatch(RemoteCollection collection, Func<ValueTask> detach)
    {
        Collection = collection;
        _detach = detach;
    }

    /// <summary>The time of an event raised now in this process (the in-process host clock).</summary>
    public static TimeSpan Now => Stopwatch.GetElapsedTime(0);

    public RemoteCollection Collection { get; }

    /// <summary>Raised after each recorded change (the fake host forwards them as events).</summary>
    public event Action<RemoteListChange<T>>? Recorded;

    public IReadOnlyList<RemoteListChange<T>> Changes
    {
        get
        {
            lock (_gate)
                return _changes.ToArray();
        }
    }

    /// <summary>Records a change the client raised.</summary>
    public void Add(RemoteListChange<T> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _changes.Add(change);
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
        Recorded?.Invoke(change);
    }

    public async ValueTask<IReadOnlyList<RemoteListChange<T>>> WaitForAsync(Func<IReadOnlyList<RemoteListChange<T>>, bool> predicate, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            RemoteListChange<T>[] changes;
            Task changed;
            lock (_gate)
            {
                changes = _changes.ToArray();
                changed = _changed.Task;
            }

            if (predicate(changes))
                return changes;

            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"The change events of {Collection} did not reach the expected state within {timeout}; {Describe(changes)}.");
            try
            {
                await changed.WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Checked once more at the top, then reported.
            }
        }
    }

    public async ValueTask<IReadOnlyList<RemoteListChange<T>>> WaitForAsync(NexusCollectionChangedAction action, int count, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        try
        {
            return await WaitForAsync(c => c.Count(e => e.Action == action) >= count, timeout).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            var reached = Changes.Count(c => c.Action == action);
            throw new TimeoutException($"{action} events of {Collection}: {reached}/{count} within {timeout}. {e.Message}", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _detach().ConfigureAwait(false);
    }

    private static string Describe(IReadOnlyList<RemoteListChange<T>> changes)
        => changes.Count == 0
            ? "no events"
            : changes.Count + " event(s): " + string.Join(", ", changes.GroupBy(c => c.Action).Select(g => g.Key + "=" + g.Count()));
}
