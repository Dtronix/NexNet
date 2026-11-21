using NexNet.Collections;
using NexNet.Collections.Lists;

namespace NexNet.Interop.InProc;

/// <summary>An <see cref="IRemoteList{T}"/> over a C# client's <see cref="INexusList{T}"/>.</summary>
/// <param name="collection">The collection.</param>
/// <param name="list">The C# client's list.</param>
/// <param name="watches">Counts the watches still attached (leak report subscriptions).</param>
internal sealed class InProcRemoteList<T>(RemoteCollection collection, INexusList<T> list, InProcInvoker.WatchCounter watches) : IRemoteList<T>
{
    public RemoteCollection Collection { get; } = collection;

    public bool IsEnabled { get; private set; }

    public async ValueTask<bool> EnableAsync(RemoteCancel? cancel = null)
    {
        using var source = InProcInvoker.CreateCancellation(cancel ?? RemoteCancel.None, out var token);
        var enabled = await list.EnableAsync(token).ConfigureAwait(false);
        if (enabled)
            await list.ReadyTask.WaitAsync(token).ConfigureAwait(false);
        IsEnabled |= enabled;
        return enabled;
    }

    public async ValueTask DisableAsync()
    {
        await list.DisableAsync().ConfigureAwait(false);
        IsEnabled = false;
    }

    public async ValueTask<bool> AddAsync(T item) => await list.AddAsync(item).ConfigureAwait(false);

    public async ValueTask<bool> InsertAsync(int index, T item) => await list.InsertAsync(index, item).ConfigureAwait(false);

    public async ValueTask<bool> RemoveAsync(T item) => await list.RemoveAsync(item).ConfigureAwait(false);

    public async ValueTask<bool> RemoveAtAsync(int index) => await list.RemoveAtAsync(index).ConfigureAwait(false);

    public async ValueTask<bool> ReplaceAsync(int index, T item) => await list.ReplaceAsync(index, item).ConfigureAwait(false);

    public async ValueTask<bool> MoveAsync(int fromIndex, int toIndex) => await list.MoveAsync(fromIndex, toIndex).ConfigureAwait(false);

    public async ValueTask<bool> ClearAsync() => await list.ClearAsync().ConfigureAwait(false);

    public ValueTask<IReadOnlyList<T>> SnapshotAsync() => new(list.ToArray());

    // The C# list does not expose its mode: it is the one it was declared with, which the descriptor holds.
    public ValueTask<RemoteListState> GetStateAsync()
        => new(new RemoteListState(Collection.Mode, list.State, list.DisabledTask.IsCompleted, list.IsReadOnly));

    public ValueTask<IRemoteListWatch<T>> WatchAsync() => WatchAsync(throws: false);

    /// <summary>
    /// Starts a watch; with <paramref name="throws"/>, its handler throws <see cref="InvalidOperationException"/> after
    /// recording each change (<see cref="RemoteListExtensions.WatchThrowingAsync{T}"/>).
    /// </summary>
    public ValueTask<IRemoteListWatch<T>> WatchAsync(bool throws)
    {
        IDisposable? subscription = null;
        var watch = new RemoteListWatch<T>(Collection, () =>
        {
            subscription?.Dispose();
            watches.Decrement();
            return ValueTask.CompletedTask;
        });
        watches.Increment();
        // The C# client's event carries only the action (its args are pooled: read them in the handler).
        subscription = list.Changed.Subscribe(e =>
        {
            if (e.ChangedAction == NexusCollectionChangedAction.Unset)
                return;
            watch.Add(new RemoteListChange<T>(e.ChangedAction, RemoteListWatch<T>.Now));
            if (throws)
                throw new InvalidOperationException(RemoteListExtensions.ThrowingWatchMessage);
        });
        return new ValueTask<IRemoteListWatch<T>>(watch);
    }

    public async ValueTask<IReadOnlyList<T>> WaitForAsync(Func<IReadOnlyList<T>, bool> predicate, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = list.ToArray();
            if (predicate(snapshot))
                return snapshot;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"The collection {Collection} did not reach the expected state within {timeout}; it has {snapshot.Length} items.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
