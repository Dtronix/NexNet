using NexNet.Inspector.Core.Format;
using NexNet.Pipes;

namespace NexNet.Inspector.Tap;

/// <summary>
/// A tap stream from an inspector endpoint: <c>.nntl</c> blocks in file order. Read it block by block with
/// <see cref="ReadAsync"/>, or copy it verbatim into a file with <see cref="CopyToAsync"/> (the result is a valid
/// recording; it has its index footer when the stream ended cleanly, and a crash-tolerant reader handles the rest).
/// Use one or the other, not both.
/// </summary>
public sealed class InspectorStream : IAsyncDisposable
{
    private readonly IRentedNexusDuplexPipe _pipe;
    private readonly NntlPipeReader _reader;

    internal InspectorStream(IRentedNexusDuplexPipe pipe)
    {
        _pipe = pipe;
        _reader = new NntlPipeReader(pipe.Input);
    }

    /// <summary>The block read last; its body is valid until the next read.</summary>
    public NntlBlock Current => _reader.Current;

    /// <summary>True once the stream ended with its footer.</summary>
    public bool IsComplete => _reader.IsComplete;

    /// <summary>Reads the next block.</summary>
    /// <returns>False at the end of the stream.</returns>
    public ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default) => _reader.ReadAsync(cancellationToken);

    /// <summary>Copies the raw stream into <paramref name="destination"/> until it ends.</summary>
    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default) =>
        _pipe.Input.CopyToAsync(destination, cancellationToken);

    /// <summary>Releases the pipe; the endpoint stops streaming if it has not already.</summary>
    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
