using System.IO.Pipelines;

namespace NexNet.Inspector.Tap;

/// <summary>Where a <see cref="TapSink"/> sends its <c>.nntl</c> stream.</summary>
internal abstract class SinkOutput
{
    /// <summary>The stream the sink's <c>NntlWriter</c> appends to. Writes must not block on the network.</summary>
    public abstract Stream Stream { get; }

    /// <summary>Pushes what was written. Waiting here is what slows a sink down when its destination is slow.</summary>
    /// <param name="idle">True when the sink has nothing more to write for now.</param>
    /// <param name="cancellationToken">Canceled when a stopping sink's deadline passes; a canceled flush ends the output.</param>
    /// <returns>False when the destination is gone (or a flush was canceled) and nothing more may be written.</returns>
    public abstract ValueTask<bool> FlushAsync(bool idle, CancellationToken cancellationToken);

    /// <summary>Ends the output after the last write.</summary>
    public abstract ValueTask CompleteAsync();
}

/// <summary>Streams to a viewer over a NexNet duplex pipe; the pipe's flow control paces the sink.</summary>
internal sealed class PipeSinkOutput(PipeWriter writer) : SinkOutput
{
    private readonly PipeWriterStream _stream = new(writer);

    public override Stream Stream => _stream;

    public override async ValueTask<bool> FlushAsync(bool idle, CancellationToken cancellationToken)
    {
        try
        {
            // A NexNet pipe writer drops what a canceled flush had not sent yet, possibly part of a block: the stream
            // cannot go on after one.
            var result = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return !result.IsCompleted && !result.IsCanceled;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override async ValueTask CompleteAsync()
    {
        try
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The viewer is gone; there is nothing left to tell it.
        }
    }
}

/// <summary>Records into an application stream (a file, a memory stream), flushing on a schedule for durability.</summary>
internal sealed class StreamSinkOutput(Stream stream, bool leaveOpen, TimeSpan flushInterval) : SinkOutput
{
    private long _lastFlush = Environment.TickCount64;

    public override Stream Stream => stream;

    public override ValueTask<bool> FlushAsync(bool idle, CancellationToken cancellationToken)
    {
        var now = Environment.TickCount64;
        if (idle || now - _lastFlush >= (long)flushInterval.TotalMilliseconds)
        {
            stream.Flush();
            _lastFlush = now;
        }

        return new ValueTask<bool>(true);
    }

    public override ValueTask CompleteAsync()
    {
        if (!leaveOpen)
            return stream.DisposeAsync();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A write-only <see cref="System.IO.Stream"/> that copies into a <see cref="PipeWriter"/> without flushing, so the
/// synchronous <c>NntlWriter</c> never blocks; the sink flushes the pipe asynchronously after each batch.
/// </summary>
internal sealed class PipeWriterStream(PipeWriter writer) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var span = writer.GetSpan(Math.Min(buffer.Length, 64 * 1024));
            var n = Math.Min(span.Length, buffer.Length);
            buffer.Slice(0, n).CopyTo(span);
            writer.Advance(n);
            buffer = buffer.Slice(n);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
