using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace NexNet.Internals.Collections.Lists;

internal class LockFreeArrayList<T> : IEnumerable<T>
{
    private const int NullIndex = -1;

    private struct Node
    {
        public T Value;
        public int Next; // index of next node in the main list
        public int Marked; // 0 = live, 1 = logically deleted
        public int FreeNext; // index of next node in the free‑list
    }

    private volatile Node[] _nodes;
    private volatile int _count; // next slot if free list empty
    private volatile int _freeListHead; // head of reclaimed indices

    public LockFreeArrayList(int initialCapacity = 16)
    {
        _nodes = new Node[initialCapacity];
        _nodes[0].Next = NullIndex;
        _count = 1;
        _freeListHead = NullIndex;
    }

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

            if (Interlocked.CompareExchange(
                    ref nodes[0].Next,
                    newIdx,
                    oldNext
                ) == oldNext)
            {
                return;
            }
            // lost race, retry
        }
    }

    /// <summary>
    /// Inserts <paramref name="item"/> so that it becomes the element at position
    /// <paramref name="index"/> in the list (0‑based).  Throws if index is out of range.
    /// </summary>
    public void Insert(int index, T item)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));

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
            if (i != index)
                throw new ArgumentOutOfRangeException(nameof(index));

            // 2) grab a free slot (or grow & allocate):
            int newIdx = PopFreeIndex();
            if (newIdx == NullIndex)
                newIdx = AllocateNewSlot(item);
            else
                InitializeNode(newIdx, item);

            // 3) splice it in between prevIdx → currIdx
            var freshNodes = _nodes; // re‑read in case of a resize
            freshNodes[newIdx].Next = currIdx; // link forward
            if (Interlocked.CompareExchange(
                    ref freshNodes[prevIdx].Next, // location to update
                    newIdx, // our new node
                    currIdx // what we expect was there
                ) == currIdx)
            {
                // success!
                return;
            }

            // 4) lost the race on that CAS: clean up and retry
            PushFreeIndex(newIdx);
        }
    }

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
            if (Interlocked.CompareExchange(
                    ref nodes[currIdx].Marked,
                    1,
                    0
                ) != 0)
            {
                continue; // someone else deleted; retry
            }

            // 3) physical unlink
            int nextIdx = nodes[currIdx].Next;
            if (Interlocked.CompareExchange(
                    ref nodes[prevIdx].Next,
                    nextIdx,
                    currIdx
                ) == currIdx)
            {
                // reclaimed: push currIdx onto free‑list
                PushFreeIndex(currIdx);
                return true;
            }
            // unlink lost race; retry entire remove
        }
    }

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

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();


    // ─── free‑list operations ────────────────────────────────────────────────

    private int PopFreeIndex()
    {
        while (true)
        {
            int head = _freeListHead;
            if (head == NullIndex)
                return NullIndex;

            int next = _nodes[head].FreeNext;
            if (Interlocked.CompareExchange(
                    ref _freeListHead,
                    next,
                    head
                ) == head)
            {
                return head;
            }
        }
    }

    private void PushFreeIndex(int idx)
    {
        while (true)
        {
            int oldHead = _freeListHead;
            _nodes[idx].FreeNext = oldHead;
            if (Interlocked.CompareExchange(
                    ref _freeListHead,
                    idx,
                    oldHead
                ) == oldHead)
            {
                return;
            }
        }
    }


    // ─── slot allocation & initialization ────────────────────────────────────

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

    private void InitializeNode(int idx, T item)
    {
        var nodes = _nodes;
        nodes[idx].Value = item;
        nodes[idx].Next = NullIndex;
        nodes[idx].Marked = 0;
        nodes[idx].FreeNext = NullIndex;
    }

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

            if (Interlocked.CompareExchange(
                    ref _nodes,
                    newArr,
                    oldArr
                ) == oldArr)
            {
                return;
            }
        }
    }
}
