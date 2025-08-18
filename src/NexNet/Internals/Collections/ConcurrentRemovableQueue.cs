using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace NexNet.Internals.Collections;

internal class ConcurrentRemovableQueue<T> : IEnumerable<T>
{
    private sealed class Node
    {
        internal readonly T Item;
        internal Node? Next; // Write-once when linking forward; never mutated on removal
        internal Node? Prev; // Updated when linking/unlinking (not needed by enumerator)

        private int _removed; // 0 = present, 1 = tombstoned

        internal Node(T item) => Item = item;

        internal bool IsRemoved => Volatile.Read(ref _removed) != 0;
        internal bool TryMarkRemoved() => Interlocked.Exchange(ref _removed, 1) == 0;
    }

    // --- State ----------------------------------------------------------------
    private readonly Lock _gate = new Lock();
    private readonly IEqualityComparer<T> _comparer;
    private Node? _head;
    private Node? _tail;
    private int _count;

    // Value -> nodes containing that value (Node identity as key element)
    private readonly Dictionary<T, HashSet<Node>> _index;

    public ConcurrentRemovableQueue() : this(EqualityComparer<T>.Default) { }

    public ConcurrentRemovableQueue(IEqualityComparer<T> comparer)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _index = new Dictionary<T, HashSet<Node>>(_comparer);
    }

    public int Count { get { lock (_gate) return _count; } }
    
    public void Enqueue(T item)
    {
        var node = new Node(item);

        lock (_gate)
        {
            if (_tail == null)
            {
                _head = _tail = node;
            }
            else
            {
                node.Prev = _tail;
                _tail.Next = node;   // write-once forward link
                _tail = node;
            }

            if (!_index.TryGetValue(item, out var set))
            {
                set = new HashSet<Node>();
                _index[item] = set;
            }
            set.Add(node);

            _count++;
        }
    }

    public bool TryDequeue(out T item)
    {
        lock (_gate)
        {
            var n = _head;
            while (n != null)
            {
                var curr = n;
                n = n.Next; // follow forward chain; safe for enumerator as well

                if (curr.TryMarkRemoved())
                {
                    Unlink(curr);
                    RemoveFromIndex(curr);
                    _count--;
                    item = curr.Item;
                    return true;
                }

                // Already tombstoned; clean it out of the active chain.
                Unlink(curr);
            }
        }

        item = default!;
        return false;
    }

    public bool Remove(T item)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(item, out var set) || set.Count == 0)
                return false;

            Node? candidate = null;
            foreach (var n in set)
            {
                if (!n.IsRemoved) { candidate = n; break; }
            }

            if (candidate == null)
            {
                set.RemoveWhere(static n => n.IsRemoved);
                if (set.Count == 0) _index.Remove(item);
                return false;
            }

            if (candidate.TryMarkRemoved())
            {
                Unlink(candidate);
                set.Remove(candidate);
                if (set.Count == 0) _index.Remove(item);
                _count--;
                return true;
            }

            set.RemoveWhere(static n => n.IsRemoved);
            if (set.Count == 0) _index.Remove(item);
            return false;
        }
    }
    
    // Walks the forward links captured from _head at construction time.
    // Skips tombstoned nodes so iteration remains valid if items are removed.
    public Enumerator GetEnumerator() => new Enumerator(this);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    public struct Enumerator : IEnumerator<T>
    {
        private readonly ConcurrentRemovableQueue<T> _owner;
        private Node? _cursor;
        private T _current;

        internal Enumerator(ConcurrentRemovableQueue<T> owner)
        {
            _owner = owner;
            _cursor = Volatile.Read(ref owner._head); // start snapshot (no allocation)
            _current = default!;
        }

        public T Current => _current;
        object IEnumerator.Current => _current!;

        public bool MoveNext()
        {
            while (_cursor != null)
            {
                var n = _cursor;
                _cursor = n.Next; // Next is write-once; safe to read without locking

                if (!n.IsRemoved)
                {
                    _current = n.Item;
                    return true;
                }
                // else: skip tombstoned node and continue
            }

            _current = default!;
            return false;
        }

        public void Reset()
        {
            _cursor = Volatile.Read(ref _owner._head);
            _current = default!;
        }

        public void Dispose() { }
    }
    
    private void Unlink(Node n)
    {
        // Under _gate
        var prev = n.Prev;
        var next = n.Next;

        if (prev == null)
            _head = next;
        else
            prev.Next = next;

        if (next == null)
            _tail = prev;
        else
            next.Prev = prev;

        // IMPORTANT: do NOT null out n.Next (or n.Prev) so enumerators can keep walking
        // using the forward link even if this node was removed after they started.
    }

    private void RemoveFromIndex(Node n)
    {
        if (_index.TryGetValue(n.Item, out var set))
        {
            set.Remove(n);
            if (set.Count == 0) _index.Remove(n.Item);
        }
    }
}
