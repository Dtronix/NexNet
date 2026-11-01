#nullable disable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NexNet.Internals.Pipelines.Internal;

namespace NexNet.Internals.Pipelines.Buffers;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over a chain of reference-counted segments rented from
/// <see cref="ArrayPool{T}.Shared"/>. The buffered data is read as a <see cref="ReadOnlySequence{T}"/>
/// (<see cref="GetBuffer"/>); consumed data is returned to the pool from the head (<see cref="ReleaseTo(int)"/>,
/// <see cref="ReleaseTo(SequencePosition)"/>, <see cref="Deallocate"/>) while writing continues at the tail.
/// </summary>
/// <remarks>
/// Derived from Pipelines.Sockets.Unofficial (MIT, Marc Gravell; see LICENSE in this folder), reduced to bytes and
/// <see cref="ReadOnlySequence{T}"/>.
/// </remarks>
internal sealed class BufferWriter : IDisposable, IBufferWriter<byte>
{
    private const int DefaultBlockSize = 8 * 1024;

    private readonly int _blockSize;

    // Released segments are reused by this writer.
    private readonly ConcurrentBag<RefCountedSegment> _segmentPool = new();

    private RefCountedSegment _head, _tail, _final;
    private int _headOffset, _tailOffset, _tailRemaining;

    private BufferWriter(int blockSize)
    {
        if (blockSize <= 0) Throw.ArgumentOutOfRange(nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>
    /// Creates a writer renting blocks of at least <paramref name="blockSize"/> bytes (default 8 KiB).
    /// </summary>
    public static BufferWriter Create(int? blockSize = null) => new(blockSize ?? DefaultBlockSize);

    /// <summary>
    /// Gets the amount of data buffered by the writer
    /// </summary>
    public long Length => _head is null ? 0 :
        _tail.RunningIndex + _tailOffset - (_head.RunningIndex + _headOffset);

    /// <summary>
    /// Release all resources associate with this instance
    /// </summary>
    public void Dispose() => Reset();

    /// <summary>
    /// Release all resources associate with this instance and reset it to its initial state.
    /// </summary>
    public void Reset()
    {
        // release anything that is in the pending buffer
        var node = _head;
        _head = _tail = _final = null;
        _tailRemaining = _tailOffset = _headOffset = 0;
        while (node is not null)
        {
            var next = node.NextSegment; // need to do this *first*, since Release nukes it
            node.Release();
            node = next;
        }
    }

    /// <summary>
    /// Gets the currently buffered data.
    /// </summary>
    public ReadOnlySequence<byte> GetBuffer()
    {
        // is it a trivial sequence? (this includes the null case)
        if (ReferenceEquals(_head, _tail) && _tailOffset == _headOffset) return default;

        return new ReadOnlySequence<byte>(_head, _headOffset, _tail, _tailOffset);
    }

    /// <summary>
    /// Releases <paramref name="value"/>, a sequence returned by <see cref="GetBuffer"/>, once it is no longer
    /// needed: the head moves to its end, and its segments are returned to the pool. Data written after
    /// <see cref="GetBuffer"/> stays buffered.
    /// </summary>
    public void Deallocate(in ReadOnlySequence<byte> value)
    {
        if (value.IsEmpty) return; // "all of nothing"

        // An end at the very end of a segment that has a successor is the start of that successor.
        var endSegment = (RefCountedSegment)value.End.GetObject();
        var endOffset = value.End.GetInteger();
        if (endOffset == endSegment.Length && endSegment.NextSegment is not null)
        {
            endSegment = endSegment.NextSegment;
            endOffset = 0;
        }

        _head = endSegment;
        _headOffset = endOffset;
        // we have some capacity left in the head; we'll keep that one, so:
        // increment the tail and continue from there
        // this is a short-cut for:
        // - AddRef on all the elements in the result
        // - Release on everything in the old chain *except* the new head (if it will be shared)
        if (_headOffset != 0) _head.AddRef();

        // Release the refcounted segments back to the pool.
        RefCountedSegment.Release(value);
    }

    /// <summary>
    /// Releases the specified number of bytes from the head of the buffer.
    /// If the count is greater than the data buffered, throws.
    /// </summary>
    /// <param name="count">Number of bytes to release</param>
    public void ReleaseTo(int count)
    {
        if (count < 1)
            return;

        if (count > Length)
            throw new ArgumentOutOfRangeException(nameof(count), $"The release of {count} elements would exceed the maximum number of {Length} remaining elements.");

        var node = _head;

        count += _headOffset;

        if (node is not null && count < node.Length)
        {
            _headOffset = count;
            return;
        }

        do
        {
            if (count < node.Length | // inside this segment
                node.Next is null & count == node.Length) // EOF in final segment
            {
                break;
            }

            // This node has entirely been used.  Release the data.
            count -= node.Length;
            var next = node.NextSegment;
            node.Release();
            node = next;
        } while (node is not null);

        _head = node;
        _headOffset = count;
    }

    /// <summary>
    /// Releases the data before <paramref name="position"/>, a position in a sequence from <see cref="GetBuffer"/>.
    /// </summary>
    public void ReleaseTo(SequencePosition position)
    {
        if (position.GetObject() is not RefCountedSegment target)
            return;

        var node = _head;

        while (true)
        {
            if (node == target || node.Next is null) // EOF or target
                break;

            // This node has entirely been used.  Release the data.
            var next = node.NextSegment;
            node.Release();
            node = next;
        }

        _head = node;
        _headOffset = position.GetInteger();
    }

    /// <summary>
    /// Commit a number of bytes to the underyling buffer
    /// </summary>
    public void Advance(int count)
    {
        if (count < 0)
        {
            Throw.ArgumentOutOfRange(nameof(count));
        }
        else if (count == 0)
        {
            // nothing to do
        }
        else if (count <= _tailRemaining)
        {
            // single-block take
            _tailRemaining -= count;
            _tailOffset += count;
        }
        else
        {
            // multi-block take
            SlowAdvance(count);
        }
    }

    internal string GetState() // used for testing only; doesn't need to be efficient
    {
        if (_tail is null) return "(nil)";
        var sb = new StringBuilder();
        sb.Append($"[{_head.RunningIndex + _headOffset},{_tail.RunningIndex + _tailOffset}) - {_tailRemaining}/{_final.RunningIndex + _final.Length - (_tail.RunningIndex + _tailOffset)} available; counts: ");
        var node = _head;
        while (node is not null)
        {
            sb.Append(node.RefCount).Append('/');
            node = node.NextSegment;
        }
        return sb.ToString();
    }

    private void SlowAdvance(int count)
    {
        // consume the current node
        count -= _tailRemaining;
        var node = _tail.NextSegment;

        // consume nodes until the required data fits
        while (node is not null & count > node.Length)
        {
            count -= node.Length;
            node = node.NextSegment;
        }
        if (node is null) Throw.ArgumentOutOfRange(nameof(count));

        // consume part of the final node
        _tail = node;
        _tailOffset = count;
        _tailRemaining = node.Length - count;
    }

    /// <summary>
    /// Access a contiguous write buffer
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint)
    {
        sizeHint = Math.Max(sizeHint, 1);
        return _tailRemaining >= sizeHint ? _tail.WritableMemory.Span.Slice(_tailOffset) : GetMemorySlow(sizeHint).Span;
    }

    /// <summary>
    /// Access a contiguous write buffer
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint)
    {
        sizeHint = Math.Max(sizeHint, 1);
        return _tailRemaining >= sizeHint ? _tail.WritableMemory.Slice(_tailOffset) : GetMemorySlow(sizeHint);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Memory<byte> GetMemorySlow(int sizeHint)
    {
        Debug.Assert(sizeHint > _tailRemaining, "shouldn't have called slow impl");

        // limit the tail to the committed bytes
        _tail?.Trim(_tailOffset);
        var wasHead = ReferenceEquals(_tail, _head);

        var next = _tail?.NextSegment;
        if (next is not null)
        {   // we already have an onwards chain
            _tail = next;
        }
        else
        {   // rent new block in the chain
            _final = _tail = CreateNewSegment(_final, Math.Max(sizeHint, _blockSize));
        }

        if (_head is null) { _head = _tail; }
        else if (wasHead && _head.Length == _headOffset)
        {
            // the old head had capacity that we couldn't use and was trimmed; we can
            // release the old head and start from the next - this avoids starting with
            // a zero-length segment, which would never be released when sequences
            // roll forwards over it
            var oldHead = _head;
            _head = oldHead.NextSegment;
            _headOffset = 0;
            oldHead.Release();
        }
        _tailOffset = 0;
        _tailRemaining = _tail.Length;

        return _tail.WritableMemory;
    }

    private RefCountedSegment CreateNewSegment(RefCountedSegment previous, int size)
    {
        var array = ArrayPool<byte>.Shared.Rent(size);
        if (!_segmentPool.TryTake(out var segment))
            return new RefCountedSegment(_segmentPool, array, previous);

        segment.Setup(array, previous);
        return segment;
    }

    /// <summary>
    /// A segment over an array rented from <see cref="ArrayPool{T}.Shared"/>, returned when the last reference is
    /// released.
    /// </summary>
    internal sealed class RefCountedSegment : ReadOnlySequenceSegment<byte>
    {
        private readonly ConcurrentBag<RefCountedSegment> _segmentPool;
        private int _count;

        public RefCountedSegment(ConcurrentBag<RefCountedSegment> segmentPool, byte[] array, RefCountedSegment previous)
        {
            _segmentPool = segmentPool;
            Setup(array, previous);
        }

        internal int RefCount => Volatile.Read(ref _count);

        /// <summary>
        /// Length of this segment's memory.
        /// </summary>
        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get;
            private set;
        }

        /// <summary>
        /// The next segment of the chain, or null.
        /// </summary>
        public RefCountedSegment NextSegment
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.As<RefCountedSegment>(Next);
        }

        /// <summary>
        /// The segment's memory, writable: the buffer writer writes into it.
        /// </summary>
        public Memory<byte> WritableMemory
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => MemoryMarshal.AsMemory(Memory);
        }

        public void Setup(byte[] array, RefCountedSegment previous)
        {
            if (previous is not null)
            {
                if (previous.Next is not null) Throw.InvalidOperation("The previous segment already has an onwards chain");
                previous.Next = this;
                RunningIndex = previous.RunningIndex + previous.Length;
            }
            else
            {
                RunningIndex = 0;
            }

            SetMemory(array);
            _count = 1;
        }

        private void SetMemory(Memory<byte> memory)
        {
            Memory = memory;
            Length = memory.Length;
        }

        /// <summary>
        /// Shortens the segment to <paramref name="length"/> and moves the running index of later segments back.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Trim(int length)
        {
            long delta = Length - length;
            if (delta < 0) Throw.ArgumentOutOfRange(nameof(length));
            else if (delta != 0)
            {
                SetMemory(WritableMemory.Slice(0, length));
                // fixup the running index of any later segments
                var node = NextSegment;
                while (node is not null)
                {
                    node.RunningIndex -= delta;
                    node = node.NextSegment;
                }
            }
        }

        /// <summary>
        /// Releases one reference to every segment of <paramref name="value"/>.
        /// </summary>
        internal static void Release(ReadOnlySequence<byte> value)
        {
            if (value.IsEmpty) return;

            var start = value.Start;
            var len = value.Length + start.GetInteger(); // we'll be counting the full length of every segment, including the first
            var node = start.GetObject() as RefCountedSegment;

            while (len > 0 & node is not null)
            {
                len -= node.Length;
                var next = node.NextSegment; // need to do this *first*, since Release nukes it
                node.Release();
                node = next;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release()
        {
            if (Volatile.Read(ref _count) > 0 && Interlocked.Decrement(ref _count) == 0)
            {
                Next = null; // break the chain, in case of dangling references
                if (MemoryMarshal.TryGetArray(Memory, out var segment) && segment.Array is not null)
                    ArrayPool<byte>.Shared.Return(segment.Array);
                SetMemory(default);
                _segmentPool.Add(this);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRef()
        {
            if (Volatile.Read(ref _count) == 0) Throw.ObjectDisposed(nameof(RefCountedSegment));
            Interlocked.Increment(ref _count);
        }
    }
}
