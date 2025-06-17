using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace NexNet.Internals.Collections.Lists;

/// <summary>
/// A fixed-capacity circular list that assigns a monotonically increasing global index to each added item.
/// When capacity is exceeded, the oldest item is overwritten and its index falls off the valid range.
/// </summary>
/// <typeparam name="T">Type of items to store.</typeparam>
internal class IndexedCircularList<T> : IEnumerable<(long Index, T Item)>
    where T : class?
{
    private readonly T[] _buffer;
    private readonly int _capacity;
    private long _nextIndex;
    private int _count;

    /// <summary>
    /// Initializes a new instance with the specified capacity.
    /// </summary>
    /// <param name="capacity">Maximum number of elements to retain.</param>
    public IndexedCircularList(int capacity = 10)
    {
        if (capacity <= 0)
            throw new ArgumentException("Capacity must be greater than zero.", nameof(capacity));

        _capacity = capacity;
        _buffer = new T[capacity];
        _nextIndex = 0;
        _count = 0;
    }

    /// <summary>
    /// Current number of elements stored (&lt;= Capacity).
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Global index of the oldest stored element.
    /// </summary>
    public long FirstIndex => _nextIndex - _count;

    /// <summary>
    /// Global index assigned to the most recently added element.
    /// </summary>
    public long LastIndex => _nextIndex - 1;
    
    /// <summary>
    /// Adds an item, assigns it a global index, and returns that index.
    /// If the buffer is full, overwrites the oldest entry.
    /// </summary>
    /// <param name="item">Item to add.</param>
    /// <returns>Global index of the added item.</returns>
    public long Add(T item)
    {
        long index = _nextIndex;
        _buffer[index % _capacity] = item;
        _nextIndex++;

        if (_count < _capacity)
            _count++;

        return index;
    }

    public void Clear()
    {
        _count = 0;
        Array.Clear(_buffer, 0, _buffer.Length);
    }
    
    public void Reset(int nextIndex = 0)
    {
        Clear();
        _nextIndex = nextIndex;
    }

    /// <summary>
    /// Retrieves the item at the given global index.
    /// Valid indices are in the range [FirstIndex, LastIndex].
    /// </summary>
    /// <param name="index">Global index of the item.</param>
    /// <returns>The stored item.</returns>
    public T this[long index]
    {
        get
        {
            long start = FirstIndex;
            long end = LastIndex;

            if (index < start || index > end)
                throw new IndexOutOfRangeException(
                    $"Index out of range: {index}. Valid range: [{start}, {end}]");

            return _buffer[index % _capacity];
        }
    }

    /// <summary>
    /// Validates a passed index.
    /// </summary>
    /// <param name="index">Index to validate</param>
    /// <returns>True if the passed index is contained in the list, false otherwise.</returns>
    public bool ValidateIndex(long index)
    {
        return !(index < FirstIndex || index > LastIndex);
    }
        
        
    /// <summary>
    /// Attempts to retrieve the value associated with the index.
    /// </summary>
    /// <param name="index">Value Index to attempt retrieval.</param>
    /// <param name="value">Value to retrieve.  Null if the method returns false.</param>
    /// <returns>False if the index is inside the current bounds, false otherwise.</returns>
    public bool TryGetValue(long index, out T? value)
    {
        if (index < FirstIndex || index > LastIndex)
        {
            value = null;
            return false;
        }

        value = _buffer[index % _capacity];
        return true;
    }

    /// <summary>
    /// Enumerates the stored items along with their global indices, from oldest to newest.
    /// </summary>
    public IEnumerator<(long Index, T Item)> GetEnumerator()
    {
        for (long i = FirstIndex; i <= LastIndex; i++)
            yield return (i, this[i]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
