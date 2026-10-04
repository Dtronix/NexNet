using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace NexNet.Serialization;

/// <summary>
/// Contiguous <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}.Shared"/>, itself pooled.
/// Used for pre-serialized argument and value buffers.
/// </summary>
public sealed class PooledArrayBufferWriter : IBufferWriter<byte>
{
    private const int DefaultInitialSize = 256;
    private const int MaxPooledInstances = 64;
    private const int MaxRetainedBufferSize = 1024 * 1024;

    private static readonly ConcurrentQueue<PooledArrayBufferWriter> _pool = new();
    private static int _poolCount;

    private byte[] _buffer;
    private int _written;

    private PooledArrayBufferWriter()
    {
        _buffer = ArrayPool<byte>.Shared.Rent(DefaultInitialSize);
    }

    /// <summary>
    /// Rents a writer. Return it with <see cref="Return"/> when the written data is no longer needed.
    /// </summary>
    public static PooledArrayBufferWriter Rent()
    {
        if (_pool.TryDequeue(out var writer))
        {
            System.Threading.Interlocked.Decrement(ref _poolCount);
            return writer;
        }

        return new PooledArrayBufferWriter();
    }

    /// <summary>
    /// Resets the writer and returns it to the pool. The written memory must not be used afterwards.
    /// </summary>
    public void Return()
    {
        _written = 0;
        if (_buffer.Length > MaxRetainedBufferSize)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(DefaultInitialSize);
        }

        if (System.Threading.Interlocked.Increment(ref _poolCount) <= MaxPooledInstances)
        {
            _pool.Enqueue(this);
        }
        else
        {
            System.Threading.Interlocked.Decrement(ref _poolCount);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }
    }

    /// <summary>
    /// The bytes written so far.
    /// </summary>
    public Memory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>
    /// The bytes written so far.
    /// </summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>
    /// Number of bytes written.
    /// </summary>
    public int WrittenCount => _written;

    /// <summary>
    /// Clears the written data without returning the writer.
    /// </summary>
    public void Clear() => _written = 0;

    /// <inheritdoc />
    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _written))
            throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1)
            sizeHint = 1;

        if (_buffer.Length - _written < sizeHint)
            Grow(sizeHint);
    }

    private void Grow(int sizeHint)
    {
        var newSize = Math.Max(Math.Max(_buffer.Length * 2, _written + sizeHint), DefaultInitialSize);
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(newBuffer);
        if (_buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }
}
