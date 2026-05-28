using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// A pool of stream transports for efficient reuse.
/// </summary>
internal sealed class NexusStreamTransportPool : INexusStreamTransportPool
{
    /// <summary>
    /// Default maximum pool size.
    /// </summary>
    public const int DefaultMaxSize = 16;

    private readonly ConcurrentBag<NexusStreamTransport> _pool;
    private readonly Func<NexusStreamTransport> _factory;
    private readonly int _maxSize;
    private int _count;
    private bool _disposed;

    /// <summary>
    /// Creates a new transport pool with the specified factory and maximum size.
    /// </summary>
    /// <param name="factory">Factory function to create new transports.</param>
    /// <param name="maxSize">Maximum number of transports to pool.</param>
    public NexusStreamTransportPool(Func<NexusStreamTransport> factory, int maxSize = DefaultMaxSize)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (maxSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSize), "Max size must be greater than zero.");

        _pool = new ConcurrentBag<NexusStreamTransport>();
        _maxSize = maxSize;
    }

    /// <inheritdoc />
    public int Count => _count;

    /// <inheritdoc />
    public int MaxSize => _maxSize;

    /// <inheritdoc />
    public IRentedNexusStreamTransport Rent()
    {
        ThrowIfDisposed();

        NexusStreamTransport transport;

        if (_pool.TryTake(out var pooled))
        {
            Interlocked.Decrement(ref _count);
            transport = pooled;
        }
        else
        {
            transport = _factory();
        }

        return new RentedNexusStreamTransport(transport, this);
    }

    /// <inheritdoc />
    public void Return(NexusStreamTransport transport)
    {
        if (_disposed)
        {
            // Pool is disposed - dispose the transport instead
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        // Check if transport is still in a valid state for reuse
        if (transport.State != NexusStreamState.None && transport.State != NexusStreamState.Closed)
        {
            // Transport is in an inconsistent state - dispose it
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        // Check if we're at capacity
        if (_count >= _maxSize)
        {
            // Pool is full - dispose the transport
            transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        Interlocked.Increment(ref _count);
        _pool.Add(transport);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Dispose all pooled transports
        while (_pool.TryTake(out var transport))
        {
            Interlocked.Decrement(ref _count);
            try
            {
                transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NexusStreamTransportPool));
    }
}
