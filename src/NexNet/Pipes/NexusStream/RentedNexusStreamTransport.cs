using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// A rented stream transport that returns to its pool when disposed.
/// </summary>
internal sealed class RentedNexusStreamTransport : IRentedNexusStreamTransport
{
    private readonly NexusStreamTransport _inner;
    private readonly INexusStreamTransportPool? _pool;
    private bool _returned;

    /// <summary>
    /// Creates a new rented transport wrapper.
    /// </summary>
    /// <param name="inner">The underlying transport.</param>
    /// <param name="pool">The pool to return to on dispose.</param>
    public RentedNexusStreamTransport(NexusStreamTransport inner, INexusStreamTransportPool? pool)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _pool = pool;
    }

    /// <summary>
    /// Gets the pool this transport was rented from, or null if not pooled.
    /// </summary>
    internal INexusStreamTransportPool? Pool => _pool;

    /// <inheritdoc />
    public bool IsReturned => _returned;

    /// <inheritdoc />
    public Task ReadyTask => _inner.ReadyTask;

    /// <inheritdoc />
    public ValueTask<INexusStream> OpenAsync(
        string resourceId,
        StreamAccessMode access,
        StreamShareMode share = StreamShareMode.None,
        long resumePosition = -1,
        CancellationToken ct = default)
    {
        ThrowIfReturned();
        return _inner.OpenAsync(resourceId, access, share, resumePosition, ct);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<INexusStreamRequest> ReceiveRequestsAsync(CancellationToken ct = default)
    {
        ThrowIfReturned();
        return _inner.ReceiveRequestsAsync(ct);
    }

    /// <inheritdoc />
    public ValueTask ProvideFileAsync(string path, FileShare fileShare = FileShare.Read, CancellationToken ct = default)
    {
        ThrowIfReturned();
        return _inner.ProvideFileAsync(path, fileShare, ct);
    }

    /// <inheritdoc />
    public ValueTask ProvideStreamAsync(Stream stream, CancellationToken ct = default)
    {
        ThrowIfReturned();
        return _inner.ProvideStreamAsync(stream, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_returned)
            return;

        _returned = true;

        if (_pool != null)
        {
            // Return to pool for reuse
            _pool.Return(_inner);
        }
        else
        {
            // No pool - dispose the inner transport
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfReturned()
    {
        if (_returned)
            throw new ObjectDisposedException(nameof(RentedNexusStreamTransport), "Transport has been returned to the pool.");
    }
}
