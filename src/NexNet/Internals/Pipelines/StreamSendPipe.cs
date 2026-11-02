using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Internals.Pipelines;

/// <summary>
/// Output side of a stream-backed transport (TLS, QUIC, HttpSocket, WebSocket). The session writes into a
/// <see cref="Pipe"/>; a send loop writes the pipe's contents to the stream. A flush completes once the data is in the
/// pipe (below its pause threshold) rather than when the stream write completes, so the session's write mutex is not
/// held across stream writes, and frames written while a stream write is in progress go out together in the next
/// one. The TCP and Unix domain socket transport uses it too, over a NetworkStream.
/// </summary>
/// <remarks>
/// When the stream write fails or the pipe is aborted, the pipe reader is completed, so the session's next flush
/// returns <see cref="FlushResult.IsCompleted"/> and the session disconnects.
/// </remarks>
internal sealed class StreamSendPipe
{
    /// <summary>
    /// Segments of a multi-segment read are copied into one buffer of up to this size per stream write, so a batch
    /// of small frames becomes one write (one TLS record run, one QUIC send, one WebSocket message) instead of one per
    /// pipe segment.
    /// </summary>
    internal const int CoalesceSize = 64 * 1024;

    /// <summary>
    /// How long a graceful close waits for buffered data to reach the stream. Real time: it waits on network I/O.
    /// </summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Stream _stream;
    private readonly Pipe _pipe;
    private readonly SendWriter _writer;
    private readonly CancellationTokenSource _abort = new();
    private readonly Task _sendLoop;
    private byte[]? _coalesceBuffer;

    /// <param name="stream">Stream the send loop writes to. Not disposed by this class.</param>
    /// <param name="options">Options of the send pipe; its pause threshold bounds the data buffered ahead of the
    /// stream. Null uses <see cref="PipeOptions.Default"/>.</param>
    public StreamSendPipe(Stream stream, PipeOptions? options)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _pipe = new Pipe(options ?? PipeOptions.Default);
        _writer = new SendWriter(_pipe.Writer);
        // Off the constructor's thread and outside any synchronization context.
        _sendLoop = Task.Run(SendLoopAsync);
    }

    /// <summary>
    /// The writer the session writes frames to.
    /// </summary>
    public PipeWriter Writer => _writer;

    /// <summary>
    /// Completes when the send loop has ended: the writer was completed and everything was written, the stream
    /// failed, or the pipe was aborted.
    /// </summary>
    public Task Completion => _sendLoop;

    /// <summary>
    /// For a graceful close: if the writer was completed, waits up to <paramref name="timeout"/> for the buffered
    /// data to be written, then aborts whatever remains. If the writer was not completed (no graceful shutdown by the
    /// session), aborts immediately.
    /// </summary>
    public async ValueTask DrainAsync(TimeSpan timeout)
    {
        if (_writer.IsCompleted && !_sendLoop.IsCompleted)
        {
            using var delayCancellation = new CancellationTokenSource();
            var finished = await Task.WhenAny(_sendLoop, Task.Delay(timeout, delayCancellation.Token))
                .ConfigureAwait(false);
            if (finished == _sendLoop)
                await delayCancellation.CancelAsync().ConfigureAwait(false);
        }

        Abort();
        await _sendLoop.ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the send loop without writing what is buffered. Cancels a stream write in progress.
    /// </summary>
    public void Abort()
    {
        try
        {
            _abort.Cancel();
        }
        catch (AggregateException)
        {
            // A callback registered on the token threw; the loop still observes the cancellation.
        }
    }

    private async Task SendLoopAsync()
    {
        var reader = _pipe.Reader;
        var cancellationToken = _abort.Token;
        Exception? error = null;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                try
                {
                    if (!buffer.IsEmpty)
                    {
                        if (buffer.IsSingleSegment)
                            await _stream.WriteAsync(buffer.First, cancellationToken).ConfigureAwait(false);
                        else
                            await WriteSegmentsAsync(buffer, cancellationToken).ConfigureAwait(false);

                        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    reader.AdvanceTo(buffer.End);
                }

                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Aborted by a close: not an error.
        }
        catch (ObjectDisposedException)
        {
            // The transport disposed the stream: not an error.
        }
        catch (IOException e)
        {
            error = e;
        }
        catch (Exception e)
        {
            error = new IOException(e.Message, e);
        }
        finally
        {
            // Completing the reader makes the session's next flush return IsCompleted (or throw the error).
            await reader.CompleteAsync(error).ConfigureAwait(false);
            if (_coalesceBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_coalesceBuffer);
                _coalesceBuffer = null;
            }
        }
    }

    private async ValueTask WriteSegmentsAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
    {
        var coalesce = _coalesceBuffer ??= ArrayPool<byte>.Shared.Rent(CoalesceSize);
        var pending = 0;
        foreach (var segment in buffer)
        {
            // A segment at least as large as the coalescing buffer is written as is.
            if (pending == 0 && segment.Length >= CoalesceSize)
            {
                await _stream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var remaining = segment;
            while (!remaining.IsEmpty)
            {
                var count = Math.Min(CoalesceSize - pending, remaining.Length);
                remaining.Span.Slice(0, count).CopyTo(coalesce.AsSpan(pending));
                pending += count;
                remaining = remaining.Slice(count);

                if (pending == CoalesceSize)
                {
                    await _stream.WriteAsync(coalesce.AsMemory(0, pending), cancellationToken).ConfigureAwait(false);
                    pending = 0;
                }
            }
        }

        if (pending > 0)
            await _stream.WriteAsync(coalesce.AsMemory(0, pending), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The pipe's writer, recording whether it was completed so a close knows whether the session finished writing.
    /// </summary>
    private sealed class SendWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private volatile bool _completed;

        public SendWriter(PipeWriter inner) => _inner = inner;

        public bool IsCompleted => _completed;

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => _inner.FlushAsync(cancellationToken);

        public override void Complete(Exception? exception = null)
        {
            _completed = true;
            _inner.Complete(exception);
        }

        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            _completed = true;
            return _inner.CompleteAsync(exception);
        }

        public override bool CanGetUnflushedBytes => _inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => _inner.UnflushedBytes;
    }
}
