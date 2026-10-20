using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Wraps an <see cref="INexusStream"/> as a <see cref="Stream"/> for compatibility with
/// APIs that expect a standard .NET Stream.
/// </summary>
/// <remarks>
/// <para>
/// This wrapper provides synchronous method implementations using sync-over-async patterns.
/// While functional, this approach may cause thread pool starvation under high load.
/// Prefer using the async methods whenever possible.
/// </para>
/// <para>
/// The wrapper does not own the underlying stream and will not dispose it when this
/// wrapper is disposed.
/// </para>
/// </remarks>
public sealed class NexusStreamWrapper : Stream
{
    private readonly INexusStream _inner;
    private bool _disposed;

    /// <summary>
    /// Creates a new stream wrapper around the specified NexusStream.
    /// </summary>
    /// <param name="stream">The stream to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if stream is null.</exception>
    public NexusStreamWrapper(INexusStream stream)
    {
        _inner = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Gets the underlying NexusStream.
    /// </summary>
    public INexusStream InnerStream => _inner;

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed && _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && _inner.CanWrite;

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            if (!_inner.HasKnownLength)
                throw new NotSupportedException("Stream does not have a known length.");
            return _inner.Length;
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _inner.Position;
        }
        set
        {
            ThrowIfDisposed();
            _inner.SeekAsync(value, SeekOrigin.Begin).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        ThrowIfDisposed();
        _inner.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _inner.FlushAsync(cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();

        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();

        return _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();

        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        ThrowIfDisposed();

        return _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        return _inner.SeekAsync(offset, origin).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        _inner.SetLengthAsync(value).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return CopyToAsyncInternal(destination, bufferSize, cancellationToken);
    }

    private async Task CopyToAsyncInternal(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (!CanRead)
            throw new NotSupportedException("Stream does not support reading.");
        if (!destination.CanWrite)
            throw new NotSupportedException("Destination stream does not support writing.");

        var buffer = new byte[bufferSize];
        int bytesRead;

        while ((bytesRead = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Note: We do not dispose the inner stream - that's the caller's responsibility
            // This is intentional to allow the wrapper to be recreated without affecting the stream
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        // Note: We do not dispose the inner stream - that's the caller's responsibility
        _disposed = true;

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NexusStreamWrapper));
    }
}
