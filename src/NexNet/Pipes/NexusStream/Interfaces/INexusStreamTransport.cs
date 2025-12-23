using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.NexusStream;

/// <summary>
/// Provides stream transport capabilities over a NexNet duplex pipe.
/// </summary>
public interface INexusStreamTransport : IAsyncDisposable
{
    /// <summary>
    /// Gets a task that completes when the transport is ready to accept operations.
    /// </summary>
    Task ReadyTask { get; }

    /// <summary>
    /// Opens a stream to the specified resource.
    /// Throws <see cref="InvalidOperationException"/> if a stream is already open.
    /// </summary>
    /// <param name="resourceId">The identifier of the resource to open (e.g., file path).</param>
    /// <param name="access">The requested access mode.</param>
    /// <param name="share">The requested share mode.</param>
    /// <param name="resumePosition">Position to resume from, or -1 for a fresh start.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The opened stream.</returns>
    ValueTask<INexusStream> OpenAsync(
        string resourceId,
        StreamAccessMode access,
        StreamShareMode share = StreamShareMode.None,
        long resumePosition = -1,
        CancellationToken ct = default);

    /// <summary>
    /// Receives incoming stream open requests from remote peers.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of stream requests.</returns>
    IAsyncEnumerable<INexusStreamRequest> ReceiveRequestsAsync(CancellationToken ct = default);

    /// <summary>
    /// Provides a file as the response to an incoming stream request.
    /// </summary>
    /// <param name="path">The path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ProvideFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Provides a stream as the response to an incoming stream request.
    /// </summary>
    /// <param name="stream">The stream to provide.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ProvideStreamAsync(Stream stream, CancellationToken ct = default);
}
