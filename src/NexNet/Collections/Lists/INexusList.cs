using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Collections.Lists;

/// <summary>
/// A synchronized, remotely-backed list of items of type <typeparamref name="T"/>.
/// Inherits connection management from <see cref="INexusCollection"/> and supports
/// enumeration over the items.
/// </summary>
/// <typeparam name="T">The type of elements held in the list.</typeparam>
public interface INexusList<T> : INexusCollection, IEnumerable<T>
{
    /// <summary>
    /// Asynchronously removes all items from the list on the server.
    /// </summary>
    /// <returns>True if the operation was accepted by the server; false if it was a no-op.</returns>
    Task<bool> ClearAsync();
    
    /// <summary>
    /// Determines whether the list contains a specific value.
    /// </summary>
    /// <param name="item">The object to locate in the list.</param>
    /// <returns>True if the item is found; otherwise false.</returns>
    bool Contains(T item);
    
    /// <summary>
    /// Copies the entire list to an array, starting at the specified destination index.
    /// </summary>
    /// <param name="array">The destination array with zero-based indexing.</param>
    /// <param name="arrayIndex">The zero-based index at which copying begins.</param>
    /// <exception cref="ArgumentNullException">Thrown if array is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if arrayIndex is less than 0.</exception>
    /// <exception cref="ArgumentException">Thrown if there is insufficient space in the destination array.</exception>
    void CopyTo(T[] array, int arrayIndex);
    
    /// <summary>
    /// Gets the number of elements contained in the list.
    /// </summary>
    int Count { get; }
    
    /// <summary>
    /// Gets whether the list is read-only. Always false for ServerToClient mode collections.
    /// </summary>
    bool IsReadOnly { get; }
    
    /// <summary>
    /// Returns the zero-based index of the first occurrence of the specified item.
    /// </summary>
    /// <param name="item">The object to locate in the list.</param>
    /// <returns>The index of the first occurrence if found; otherwise -1.</returns>
    int IndexOf(T item);
    
    /// <summary>
    /// Asynchronously inserts an item at the specified index on the server.
    /// </summary>
    /// <param name="index">The zero-based index at which to insert the item.</param>
    /// <param name="item">The object to insert.</param>
    /// <returns>True if the operation was accepted; false if the index was invalid or a no-op.</returns>
    Task<bool> InsertAsync(int index, T item);
    
    /// <summary>
    /// Asynchronously removes the item at the specified index on the server.
    /// </summary>
    /// <param name="index">The zero-based index of the item to remove.</param>
    /// <returns>True if the operation was accepted; false if the index was invalid.</returns>
    Task<bool> RemoveAtAsync(int index);
    
    /// <summary>
    /// Asynchronously removes the first occurrence of the specified item from the list on the server.
    /// </summary>
    /// <param name="item">The object to remove.</param>
    /// <returns>True if the item was found and removed; false if not found.</returns>
    Task<bool> RemoveAsync(T item);
    
    /// <summary>
    /// Asynchronously adds an item to the end of the list on the server.
    /// </summary>
    /// <param name="item">The object to add.</param>
    /// <returns>True if the operation was accepted; false if it was a no-op.</returns>
    Task<bool> AddAsync(T item);
    
    /// <summary>
    /// Gets the element at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <returns>The element at the specified index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is out of range.</exception>
    T this[int index] { get; }

    /// <summary>
    /// Asynchronously moves an item from one index to another on the server.
    /// </summary>
    /// <param name="fromIndex">The zero-based index of the item to move.</param>
    /// <param name="toIndex">The zero-based destination index.</param>
    /// <returns>True if the operation was accepted; false if indices were invalid or a no-op.</returns>
    Task<bool> MoveAsync(int fromIndex, int toIndex);
    
    /// <summary>
    /// Asynchronously replaces the item at the specified index with a new value on the server.
    /// </summary>
    /// <param name="index">The zero-based index of the item to replace.</param>
    /// <param name="value">The new value to set.</param>
    /// <returns>True if the operation was accepted; false if the index was invalid or the value unchanged.</returns>
    Task<bool> ReplaceAsync(int index, T value);

    /// <summary>
    /// Asynchronously enables the collection to start receiving updates from the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the enable operation.</param>
    /// <returns>True if successfully enabled; false otherwise.</returns>
    public ValueTask<bool> EnableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously disables the collection to stop receiving updates from the server.
    /// </summary>
    public ValueTask DisableAsync();

    /// <summary>
    /// Gets a task that completes when the collection is disabled.
    /// </summary>
    public Task DisabledTask { get; }

    /// <summary>
    /// Gets a task that completes when the collection is ready (connected and synchronized).
    /// For server-side collections, this completes immediately.
    /// For client-side collections, this completes after the initial sync from the server.
    /// For relay collections, this completes after connecting to the parent collection.
    /// </summary>
    public Task ReadyTask => Task.CompletedTask;

    /// <summary>
    /// Gets a task that completes when the collection becomes disconnected.
    /// </summary>
    public Task DisconnectedTask => Task.CompletedTask;

    /// <summary>
    /// Configures the collection as a relay that receives data from a parent collection.
    /// Only valid for collections marked with NexusCollectionMode.Relay.
    /// </summary>
    /// <param name="connector">The connector to the parent collection.</param>
    /// <exception cref="InvalidOperationException">Thrown if the collection is not a relay.</exception>
    public void ConfigureRelay(INexusCollectionClientConnector connector)
    {
        throw new InvalidOperationException("This collection is not configured as a relay. ConfigureRelay is only valid for relay collections.");
    }

    /// <summary>
    /// Asynchronously connects to the collection and waits for it to be ready.
    /// </summary>
    /// <returns>A task that completes when the collection is connected and ready.</returns>
    public async ValueTask ConnectAsync()
    {
        await EnableAsync().ConfigureAwait(false);
        await ReadyTask.ConfigureAwait(false);
    }
}
