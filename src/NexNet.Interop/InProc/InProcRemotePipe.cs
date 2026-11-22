using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using NexNet.Pipes;

namespace NexNet.Interop.InProc;

/// <summary>
/// An <see cref="IRemotePipe"/> over a C# pipe: one the client created (<see cref="IRentedNexusDuplexPipe"/>, disposed
/// with this object), or one the server opened that a <c>pipe.hold</c> step holds (disposing this object releases the
/// hold, and the client runtime completes the pipe when its method returns).
/// </summary>
internal sealed class InProcRemotePipe : IRemotePipe
{
    private readonly TaskCompletionSource? _released;
    private int _disposed;

    public InProcRemotePipe(IRentedNexusDuplexPipe pipe)
    {
        Pipe = pipe;
    }

    /// <summary>A held pipe of a client nexus method: <see cref="Released"/> completes when this object is disposed.</summary>
    public InProcRemotePipe(INexusDuplexPipe pipe, TaskCompletionSource released)
    {
        Pipe = pipe;
        _released = released;
    }

    public INexusDuplexPipe Pipe { get; }

    /// <summary>For a held pipe, completes when the test disposes it.</summary>
    public Task Released => _released?.Task ?? Task.CompletedTask;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ValueTask WhenReadyAsync() => new(Pipe.ReadyTask);

    public ValueTask WhenCompletedAsync() => new(Pipe.CompleteTask);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data)
        => await Pipe.Output.WriteAsync(data).ConfigureAwait(false);

    public async ValueTask<bool> TryWriteAsync(ReadOnlyMemory<byte> data)
    {
        var result = await Pipe.Output.WriteAsync(data).ConfigureAwait(false);
        return !result.IsCompleted;
    }

    public async ValueTask WritePatternAsync(int seed, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        const int chunk = 16 * 1024;
        for (long offset = 0; offset < length; offset += chunk)
        {
            var count = (int)Math.Min(chunk, length - offset);
            var memory = Pipe.Output.GetMemory(count);
            PipePattern.Fill(memory.Span.Slice(0, count), offset, seed);
            Pipe.Output.Advance(count);
            var result = await Pipe.Output.FlushAsync().ConfigureAwait(false);
            if (result.IsCompleted || result.IsCanceled)
                throw new InvalidOperationException("The pipe completed after " + offset + " of " + length + " pattern bytes.");
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(int minimum, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimum);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, Math.Max(1, minimum));
        while (true)
        {
            var result = await Pipe.Input.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= Math.Max(1, minimum) || result.IsCompleted || result.IsCanceled)
            {
                var taken = buffer.Slice(0, Math.Min(buffer.Length, maximum));
                var data = taken.ToArray();
                Pipe.Input.AdvanceTo(taken.End);
                return data;
            }

            Pipe.Input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public async ValueTask<PipeDigest> DrainAsync()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        while (true)
        {
            var result = await Pipe.Input.ReadAsync().ConfigureAwait(false);
            foreach (var segment in result.Buffer)
            {
                hash.AppendData(segment.Span);
                length += segment.Length;
            }

            Pipe.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        return new PipeDigest(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public ValueTask CompleteAsync() => Pipe.Output.CompleteAsync();

    public ValueTask CompleteInputAsync() => Pipe.Input.CompleteAsync();

    /// <summary>The C# client's forward (<see cref="NexusPipeForwardingExtensions.ForwardToAsync(INexusDuplexPipe, INexusDuplexPipe, CancellationToken)"/>).</summary>
    public ValueTask ForwardToAsync(InProcRemotePipe destination)
    {
        if (ReferenceEquals(destination.Pipe, Pipe))
            throw new InvalidOperationException("A pipe cannot be forwarded to itself.");
        return Pipe.ForwardToAsync(destination.Pipe);
    }

    public async ValueTask<IRemoteChannelWriter<T>> GetChannelWriterAsync<T>()
        => new ChannelWriter<T>(await Pipe.GetChannelWriter<T>().ConfigureAwait(false));

    public async ValueTask<IRemoteChannelReader<T>> GetChannelReaderAsync<T>()
        => new ChannelReader<T>(await Pipe.GetChannelReader<T>().ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_released != null)
            _released.TrySetResult();
        else
            await ((IRentedNexusDuplexPipe)Pipe).DisposeAsync().ConfigureAwait(false);
    }

    private sealed class ChannelWriter<T>(INexusChannelWriter<T> writer) : IRemoteChannelWriter<T>
    {
        public ValueTask<bool> WriteAsync(T item) => writer.WriteAsync(item);

        public ValueTask<bool> WriteManyAsync(IEnumerable<T> items) => writer.WriteAsync(items);

        public ValueTask CompleteAsync() => writer.CompleteAsync();
    }

    private sealed class ChannelReader<T>(INexusChannelReader<T> reader) : IRemoteChannelReader<T>
    {
        private readonly Queue<T> _buffered = new();
        private bool _complete;

        public ValueTask<IReadOnlyList<T>> ReadBatchAsync(int maximum) => ReadBatchAsync(maximum, RemoteCancel.None);

        public async ValueTask<IReadOnlyList<T>> ReadBatchAsync(int maximum, RemoteCancel cancel)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
            ArgumentNullException.ThrowIfNull(cancel);
            if (_buffered.Count == 0 && !_complete)
            {
                using var timer = InProcInvoker.CreateCancellation(cancel, out var token);
                var list = new List<T>();
                bool more;
                try
                {
                    // Returns false once the channel is complete and drained, or when the read is canceled.
                    more = await reader.ReadAsync<T>(list, null, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException e) when (token.IsCancellationRequested)
                {
                    throw new TaskCanceledException("The channel read was canceled.", e, token);
                }

                foreach (var item in list)
                    _buffered.Enqueue(item);
                if (!more && list.Count == 0 && token.IsCancellationRequested && !reader.IsComplete)
                    throw new TaskCanceledException("The channel read was canceled.", null, token);
                if (!more)
                    _complete = true;
            }

            var batch = new List<T>(Math.Min(maximum, _buffered.Count));
            while (batch.Count < maximum && _buffered.TryDequeue(out var item))
                batch.Add(item);
            return batch;
        }

        public async ValueTask<IReadOnlyList<T>> ReadAllAsync()
        {
            var all = new List<T>();
            while (true)
            {
                var batch = await ReadBatchAsync(int.MaxValue).ConfigureAwait(false);
                if (batch.Count == 0 && _complete)
                    return all;
                all.AddRange(batch);
            }
        }
    }
}
