using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NexNet.Collections;

namespace NexNet.Interop;

/// <summary>
/// One change event of a client's synchronized collection, as the client under test raised it (control protocol
/// §7.6, <c>list.changed</c>). Indexes and items are what the client runtime reports: the C# client reports only
/// <see cref="Action"/>, so a test that runs on the C# row must not depend on them.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
/// <param name="Action">What changed (never <see cref="NexusCollectionChangedAction.Unset"/>).</param>
/// <param name="HostTime">When the client raised the event, on the host's monotonic clock (arbitrary origin; compare only events of one host).</param>
public sealed record RemoteListChange<T>(NexusCollectionChangedAction Action, TimeSpan HostTime)
{
    /// <summary>The index of the item after the change (add, replace, move), or null when not reported.</summary>
    public int? Index { get; init; }

    /// <summary>The index of the item before the change (remove, move), or null when not reported.</summary>
    public int? OldIndex { get; init; }

    /// <summary>The new items (add, replace, move); empty when none or not reported.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>The old items (remove, replace); empty when none or not reported.</summary>
    public IReadOnlyList<T> OldItems { get; init; } = Array.Empty<T>();
}

/// <summary>
/// Records the change events of a client's synchronized collection from the moment it was created by
/// <see cref="IRemoteList{T}.WatchAsync"/> until it is disposed. Dispose it before the test ends: a watch still
/// attached counts as a leaked subscription.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public interface IRemoteListWatch<T> : IAsyncDisposable
{
    /// <summary>The changes recorded so far, in the order the client raised them.</summary>
    IReadOnlyList<RemoteListChange<T>> Changes { get; }

    /// <summary>Waits until the recorded changes satisfy <paramref name="predicate"/> and returns them.</summary>
    /// <exception cref="TimeoutException">Not satisfied within <paramref name="timeout"/>.</exception>
    ValueTask<IReadOnlyList<RemoteListChange<T>>> WaitForAsync(Func<IReadOnlyList<RemoteListChange<T>>, bool> predicate, TimeSpan timeout);

    /// <summary>
    /// Waits until at least <paramref name="count"/> changes of <paramref name="action"/> are recorded and returns every
    /// recorded change.
    /// </summary>
    /// <exception cref="TimeoutException">Fewer within <paramref name="timeout"/>; the message gives the count reached.</exception>
    ValueTask<IReadOnlyList<RemoteListChange<T>>> WaitForAsync(NexusCollectionChangedAction action, int count, TimeSpan timeout);
}
