using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NexNet.Internals.Collections.Lists;

/// <summary>
/// Represents array-based singly linked list that supports
/// point-in-time snapshots.
/// </summary>
/// <typeparam name="T">The type of elements contained in the list.</typeparam>
internal class SnapshotList<T> : IEnumerable<T>
{
    /// <summary>
    /// Represents a sentinel value indicating no valid index.
    /// </summary>
    private const int NullIndex = -1;

    /// <summary>
    /// A node in the lock-free list, containing the value and pointers
    /// for both the main list linkage and the free-list for reclaimed slots.
    /// </summary>
    private struct Node
    {
        /// <summary>
        /// The value stored in this node.
        /// </summary>
        public T Value;

        /// <summary>
        /// The index of the next node in the main list,
        /// or <see cref="NullIndex"/> if this is the last node.
        /// </summary>
        public int Next;

        /// <summary>
        /// Logical deletion marker: 0 = live, 1 = logically deleted.
        /// </summary>
        //public int Marked;
        
        public long InsertVersion;
        
        /// <summary>
        /// long.MaxValue means “still live”
        /// </summary>
        public long DeleteVersion;

        /// <summary>
        /// The index of the next node in the free-list,
        /// or <see cref="NullIndex"/> if there is none.
        /// </summary>
        public int FreeNext;
    }

    // Underlying array of nodes.
    private Node[] _nodes;

    // Next available slot if the free-list is empty.
    private int _count;

    // Next available slot if the free-list is empty.
    private int _freeListHead;
    
    private int _liveCount;
    
    private long _globalVersion = 0;

    private readonly Lock _lock = new();
    
    /// <summary>
    /// Gets (an approximate) number of live elements in the list.
    /// Because concurrent threads may be adding/removing, this is only
    /// updated on each successful operation and represents a point‐in‐time count.
    /// </summary>
    public int Count => _liveCount;


    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotList{T}"/> class
    /// with the specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">
    /// The initial size of the internal node array. Defaults to 16.
    /// </param>
    public SnapshotList(int initialCapacity = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _nodes = new Node[initialCapacity];
        _nodes[0].Next = NullIndex;
        _nodes[0].InsertVersion = Interlocked.Increment(ref _globalVersion);
        _nodes[0].DeleteVersion = long.MaxValue;
        _count = 1;
        _freeListHead = NullIndex;
    }

    /// <summary>
    /// Adds an item to the head of the list in a lock-free manner.
    /// </summary>
    /// <param name="item">The element to add to the list.</param>
    public void Add(T item)
    {
        lock (_lock)
        {
            // 1) allocate or reuse a slot
            int newIdx = PopFreeIndex();
            if (newIdx == NullIndex)
                newIdx = AllocateNewSlot(item);
            else
                InitializeNode(newIdx, item);

            // 2) insert at head
            var nodes = _nodes;
            int oldNext = nodes[0].Next;
            nodes[newIdx].Next = oldNext;
            nodes[0].Next = newIdx;
            _liveCount++;
        }
    }


    /// <summary>
    /// Inserts an item at the specified zero-based index in the list.
    /// </summary>
    /// <param name="index">The position at which to insert the item.</param>
    /// <param name="item">The element to insert into the list.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="index"/> is less than 0 or greater than the current list size.
    /// </exception>
    public void Insert(int index, T item)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        lock (_lock)
        {
            int prevIdx = 0; // start at the sentinel
            int currIdx = _nodes[0].Next;
            int i = 0;

            // walk until we've skipped `index` live nodes, or hit the tail
            while (i < index && currIdx != NullIndex)
            {
                prevIdx = currIdx;
                currIdx = _nodes[currIdx].Next;
                i++;
            }

            // if we stopped early, index was too large
            ArgumentOutOfRangeException.ThrowIfNotEqual(index, i);

            // 2) grab a free slot (or grow & allocate):
            int newIdx = PopFreeIndex();
            if (newIdx == NullIndex)
                newIdx = AllocateNewSlot(item);
            else
                InitializeNode(newIdx, item);

            // 3) splice it in between prevIdx → currIdx
            var freshNodes = _nodes; // re‑read in case of a resize
            freshNodes[newIdx].Next = currIdx; // link forward
            freshNodes[prevIdx].Next = newIdx;
            _liveCount++;
        }
    }

    /// <summary>
    /// Removes the first occurrence of the specified item from the list.
    /// </summary>
    /// <param name="item">The element to remove.</param>
    /// <returns>
    /// <c>true</c> if the item was found and removed; otherwise, <c>false</c>.
    /// </returns>
    public bool Remove(T item)
    {
        lock (_lock)
        {
            int prevIdx = 0;
            int currIdx = _nodes[0].Next;

            // 1) find target
            while (currIdx != NullIndex &&
                   !EqualityComparer<T>.Default.Equals(_nodes[currIdx].Value, item))
            {
                prevIdx = currIdx;
                currIdx = _nodes[currIdx].Next;
            }

            if (currIdx == NullIndex)
                return false;
            
            // 2) stamp delete-version before we unlink
            _nodes[currIdx].DeleteVersion = Interlocked.Increment(ref _globalVersion);

            // 3) physical unlink
            int nextIdx = _nodes[currIdx].Next;
            _nodes[prevIdx].Next = nextIdx;
            _liveCount--;
            PushFreeIndex(currIdx);
            return true;
        }
    }
    
    /// <summary>
    /// Returns an enumerator that iterates through the live elements
    /// in a snapshot of the list.
    /// </summary>
    /// <returns>An enumerator for the list.</returns>
    public IEnumerator<T> GetEnumerator()
    {   
        return new SnapshotEnumerator(Volatile.Read(ref _globalVersion), _nodes);
    }
    
    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// Attempts to pop a reclaimed node index from the free-list.  Must be used under a lock.
    /// </summary>
    /// <returns>
    /// The index of a free node ready for reuse, or <c>NullIndex</c> if none are available.
    /// </returns>
    private int PopFreeIndex()
    {
        while (true)
        {
            int head = _freeListHead;
            if (head == NullIndex)
                return NullIndex;

            int next = _nodes[head].FreeNext;
            if (Interlocked.CompareExchange(ref _freeListHead, next, head) == head)
                return head;
        }
    }

    /// <summary>
    /// Pushes a node index onto the free-list for future reuse. Must be used under a lock.
    /// </summary>
    /// <param name="idx">The node index to recycle.</param>
    private void PushFreeIndex(int idx)
    {
        while (true)
        {
            int oldHead = _freeListHead;
            _nodes[idx].FreeNext = oldHead;
            if (Interlocked.CompareExchange(ref _freeListHead, idx, oldHead) == oldHead)
                return;
        }
    }

    /// <summary>
    /// Allocates a new slot in the internal array for the specified item, 
    /// growing the array if necessary. Must be used under a lock.
    /// </summary>
    /// <param name="item">The element to store in the new slot.</param>
    /// <returns>The index of the newly allocated slot.</returns>
    private int AllocateNewSlot(T item)
    {
        // need bigger array
        if (_count == _nodes.Length)
            GrowArray(_count + 1);
        
        int idx = ++_count - 1;
        InitializeNode(idx, item);
        return idx;
    }

    /// <summary>
    /// Initializes the node at the given index with the specified value,
    /// setting all linkage and marker fields to their default states. Must be used under a lock.
    /// </summary>
    /// <param name="idx">The index of the node to initialize.</param>
    /// <param name="item">The value to store in the node.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InitializeNode(int idx, T item)
    {
        var nodes = _nodes;
        nodes[idx].Value = item;
        nodes[idx].Next = NullIndex;
        nodes[idx].InsertVersion = ++_globalVersion;
        nodes[idx].DeleteVersion = long.MaxValue;
        nodes[idx].FreeNext = NullIndex;
    }

    /// <summary>
    /// Ensures the internal array is at least the specified size,
    /// doubling its length or meeting the minimum as needed.
    /// </summary>
    /// <param name="minSize">The minimum required capacity.</param>
    private void GrowArray(int minSize)
    {
        if (_nodes.Length >= minSize)
            return;
            
        Array.Resize(ref _nodes, Math.Max(_nodes.Length * 2, minSize));
    }
    
    
    private struct SnapshotEnumerator : IEnumerator<T>
    {
        private readonly long _version;
        private readonly Node[] _snapshot;
        private T _current;
        private int _index;
        
        public T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _current;
        }
        
        object? IEnumerator.Current => Current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SnapshotEnumerator(long version, Node[] nodes)
        {
            _version = version;
            _snapshot = nodes;
            _index = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (_index == NullIndex)
                return false;

            while (_index != NullIndex)
            {
                // Move to the next index.
                _index = _snapshot[_index].Next;

                if (_index == NullIndex)
                    return false;
                
                var n = _snapshot[_index];
                // include iff it was inserted ON or BEFORE snapshotVersion
                // and deleted STRICTLY AFTER snapshotVersion
                if (Volatile.Read(ref n.InsertVersion) <= _version 
                    && Volatile.Read(ref n.DeleteVersion) > _version)
                {
                    _current = n.Value;
                    return true;
                }
                
                // If we reached here, then we skipped this item as it was removed or added after the version
                // that we are locked into.
            }
            
            return false;
        }
        
        public void Reset()
        {
            _index = 0;
        }


        public void Dispose()
        {
            // Noop
        }
    }
}
