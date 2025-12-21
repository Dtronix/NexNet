using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Collections.Lists;

/// <summary>
/// A synchronized, remotely-backed relay of items of type <typeparamref name="T"/>. from another NexusList
/// Inherits connection management from <see cref="INexusCollection"/> and supports
/// enumeration over the items.
/// </summary>
/// <typeparam name="T">The type of elements held in the list.</typeparam>
public interface INexusListRelay<T> : INexusCollection, IEnumerable<T>
{
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
    /// Returns the zero-based index of the first occurrence of the specified item.
    /// </summary>
    /// <param name="item">The object to locate in the list.</param>
    /// <returns>The index of the first occurrence if found; otherwise -1.</returns>
    int IndexOf(T item);
    
    /// <summary>
    /// Gets the element at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the element.</param>
    /// <returns>The element at the specified index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is out of range.</exception>
    T this[int index] { get; }

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
    
}
