using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NexNet.Internals.Collections.Lists;

/// <summary>
/// Represents a lock-free, array-based singly linked list that supports
/// concurrent additions, insertions, and removals without blocking.
/// </summary>
/// <typeparam name="T">The type of elements contained in the list.</typeparam>
internal class LockFreeArrayList<T> : IEnumerable<T>
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
        public int Marked;

        /// <summary>
        /// The index of the next node in the free-list,
        /// or <see cref="NullIndex"/> if there is none.
        /// </summary>
        public int FreeNext;
    }

    // Underlying array of nodes.
    private volatile Node[] _nodes;

    // Next available slot if the free-list is empty.
    private volatile int _count;

    // Next available slot if the free-list is empty.
    private volatile int _freeListHead;
    
    private volatile int _liveCount;
    
    /// <summary>
    /// Gets (an approximate) number of live elements in the list.
    /// Because concurrent threads may be adding/removing, this is only
    /// updated on each successful operation and represents a point‐in‐time count.
    /// </summary>
    public int Count => _liveCount;


    /// <summary>
    /// Initializes a new instance of the <see cref="LockFreeArrayList{T}"/> class
    /// with the specified initial capacity.
    /// </summary>
    /// <param name="initialCapacity">
    /// The initial size of the internal node array. Defaults to 16.
    /// </param>
    public LockFreeArrayList(int initialCapacity = 16)
    {
        _nodes = new Node[initialCapacity];
        _nodes[0].Next = NullIndex;
        _count = 1;
        _freeListHead = NullIndex;
    }

    /// <summary>
    /// Adds an item to the head of the list in a lock-free manner.
    /// </summary>
    /// <param name="item">The element to add to the list.</param>
    public void Add(T item)
    {
        // 1) allocate or reuse a slot
        int newIdx = PopFreeIndex();
        if (newIdx == NullIndex)
            newIdx = AllocateNewSlot(item);
        else
            InitializeNode(newIdx, item);

        // 2) insert at head
        while (true)
        {
            var nodes = _nodes;
            int oldNext = nodes[0].Next;
            nodes[newIdx].Next = oldNext;

            if (Interlocked.CompareExchange(ref nodes[0].Next, newIdx, oldNext) == oldNext)
            {
                Interlocked.Increment(ref _liveCount);
                return;
            }
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

        while (true)
        {
            // 1) snapshot the array & find insertion point:
            var nodes = _nodes;
            int prevIdx = 0; // start at the sentinel
            int currIdx = nodes[0].Next;
            int i = 0;

            // walk until we've skipped `index` live nodes, or hit the tail
            while (i < index && currIdx != NullIndex)
            {
                prevIdx = currIdx;
                currIdx = nodes[currIdx].Next;
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
            if (Interlocked.CompareExchange(ref freshNodes[prevIdx].Next, newIdx, currIdx) == currIdx)
            {
                // increment live count only on successful insertion
                Interlocked.Increment(ref _liveCount);
                return;
            }

            // 4) lost the race on that CAS: clean up and retry
            PushFreeIndex(newIdx);
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
        while (true)
        {
            var nodes = _nodes;
            int prevIdx = 0;
            int currIdx = nodes[0].Next;

            // 1) find target
            while (currIdx != NullIndex &&
                   !EqualityComparer<T>.Default.Equals(nodes[currIdx].Value, item))
            {
                prevIdx = currIdx;
                currIdx = nodes[currIdx].Next;
            }

            if (currIdx == NullIndex)
                return false;

            // 2) logical delete
            if (Interlocked.CompareExchange(ref nodes[currIdx].Marked, 1, 0) != 0)
            {
                continue; // someone else deleted; retry
            }

            // 3) physical unlink
            int nextIdx = nodes[currIdx].Next;
            if (Interlocked.CompareExchange(ref nodes[prevIdx].Next, nextIdx, currIdx) == currIdx)
            {
                // decrement live count now that we’ve removed one node
                Interlocked.Decrement(ref _liveCount);
                
                // reclaimed: push currIdx onto free‑list
                PushFreeIndex(currIdx);
                return true;
            }
            // unlink lost race; retry entire remove
        }
    }

    /// <summary>
    /// Returns an enumerator that iterates through the live elements
    /// in a snapshot of the list.
    /// </summary>
    /// <returns>An enumerator for the list.</returns>
    public IEnumerator<T> GetEnumerator()
    {
        var snapshot = _nodes;
        int curr = snapshot[0].Next;
        while (curr != NullIndex)
        {
            if (snapshot[curr].Marked == 0)
                yield return snapshot[curr].Value;
            curr = snapshot[curr].Next;
        }
    }

    /// <summary>
    /// Returns an enumerator that iterates through the live elements
    /// in a snapshot of the list.
    /// </summary>
    /// <returns>An enumerator for the list.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Attempts to pop a reclaimed node index from the free-list.
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
    /// Pushes a node index onto the free-list for future reuse.
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
    /// growing the array if necessary.
    /// </summary>
    /// <param name="item">The element to store in the new slot.</param>
    /// <returns>The index of the newly allocated slot.</returns>
    private int AllocateNewSlot(T item)
    {
        while (true)
        {
            var nodes = _nodes;
            int idx = Interlocked.Increment(ref _count) - 1;

            if (idx < nodes.Length)
            {
                InitializeNode(idx, item);
                return idx;
            }

            // need bigger array
            GrowArray(idx + 1);
        }
    }

    /// <summary>
    /// Initializes the node at the given index with the specified value,
    /// setting all linkage and marker fields to their default states.
    /// </summary>
    /// <param name="idx">The index of the node to initialize.</param>
    /// <param name="item">The value to store in the node.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InitializeNode(int idx, T item)
    {
        var nodes = _nodes;
        nodes[idx].Value = item;
        nodes[idx].Next = NullIndex;
        nodes[idx].Marked = 0;
        nodes[idx].FreeNext = NullIndex;
    }

    /// <summary>
    /// Ensures the internal array is at least the specified size,
    /// doubling its length or meeting the minimum as needed.
    /// </summary>
    /// <param name="minSize">The minimum required capacity.</param>
    private void GrowArray(int minSize)
    {
        while (true)
        {
            var oldArr = _nodes;
            if (oldArr.Length >= minSize)
                return;

            int newSize = Math.Max(oldArr.Length * 2, minSize);
            var newArr = new Node[newSize];
            Array.Copy(oldArr, newArr, oldArr.Length);

            if (Interlocked.CompareExchange(ref _nodes, newArr, oldArr) == oldArr)
                return;
        }
    }
}
