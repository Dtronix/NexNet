using System;
using System.Collections.Generic;
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
    /// Removes all items from the list on the server.
    /// </summary>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if the clear
    /// operation was accepted by the server; otherwise <c>false</c> if it was a no-op.
    /// </returns>
    Task<bool> ClearAsync();
    
    /// <summary>
    /// Determines whether the list contains a specific value.
    /// </summary>
    /// <param name="item">The object to locate in the list.</param>
    /// <returns>
    /// <c>true</c> if <paramref name="item"/> is found in the list; otherwise, <c>false</c>.
    /// </returns>
    bool Contains(T item);
    
    /// <summary>
    /// Copies the entire list to a compatible one-dimensional array, starting at the specified index of the target array.
    /// </summary>
    /// <param name="array">
    /// The destination array. Must be non-null and have zero-based indexing.
    /// </param>
    /// <param name="arrayIndex">
    /// The zero-based index in <paramref name="array"/> at which copying begins.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="array"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="arrayIndex"/> is less than 0.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the number of elements in this list is greater than the available space from
    /// <paramref name="arrayIndex"/> to the end of the destination array.
    /// </exception>
    void CopyTo(T[] array, int arrayIndex);
    
    /// <summary>
    /// Gets the number of elements contained in the list.
    /// </summary>
    int Count { get; }
    
    /// <summary>
    /// Gets a value indicating whether the list is read-only.
    /// </summary>
    /// <value>
    /// Always <c>false</c> if the collection was set up with a ServerToClient mode.
    /// </value>
    bool IsReadOnly { get; }
    
    /// <summary>
    /// Searches for the specified object and returns the zero-based index of the first occurrence
    /// within the list.
    /// </summary>
    /// <param name="item">The object to locate in the list.</param>
    /// <returns>
    /// The zero-based index of the first occurrence of <paramref name="item"/> if found; otherwise, –1.
    /// </returns>
    int IndexOf(T item);
    
    /// <summary>
    /// Inserts an item into the list at the specified index on the server.
    /// </summary>
    /// <param name="index">The zero-based index at which to insert the new item.</param>
    /// <param name="item">The object to insert.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if the insert
    /// was accepted by the server; otherwise <c>false</c> if the index was invalid or a no-op.
    /// </returns>
    Task<bool> InsertAsync(int index, T item);
    
    /// <summary>
    /// Removes the item at the specified index on the server.
    /// </summary>
    /// <param name="index">The zero-based index of the item to remove.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if the removal
    /// was accepted by the server; otherwise <c>false</c> if the index was invalid.
    /// </returns>
    Task<bool> RemoveAtAsync(int index);
    
    /// <summary>
    /// Removes the first occurrence of a specific object from the list on the server.
    /// </summary>
    /// <param name="item">The object to remove from the list.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if <paramref name="item"/>
    /// was found and removed; otherwise <c>false</c> if the item was not found.
    /// </returns>
    Task<bool> RemoveAsync(T item);
    
    /// <summary>
    /// Adds an item to the end of the list on the server.
    /// </summary>
    /// <param name="item">The object to add.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that completes with <c>true</c> if the add
    /// was accepted by the server; otherwise <c>false</c> if it was a no-op.
    /// </returns>
    Task<bool> AddAsync(T item);
    
    /// <summary>
    /// Gets the element at the specified zero-based index.
    /// </summary>
    /// <param name="index">The zero-based index of the element to get.</param>
    /// <returns>The element at the specified index.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="index"/> is less than 0 or greater than or equal to <see cref="Count"/>.
    /// </exception>
    T this[int index] { get; }
}
