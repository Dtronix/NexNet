using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using NexNet.Pipes;

namespace NexNet.Interop.InProc;

/// <summary>An <see cref="IRemotePipe"/> over a C# <see cref="IRentedNexusDuplexPipe"/>.</summary>
internal sealed class InProcRemotePipe : IRemotePipe
{
    private int _disposed;

    public InProcRemotePipe(IRentedNexusDuplexPipe pipe)
    {
        Pipe = pipe;
    }

    public IRentedNexusDuplexPipe Pipe { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ValueTask WhenReadyAsync() => new(Pipe.ReadyTask);

    public ValueTask WhenCompletedAsync() => new(Pipe.CompleteTask);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data)
        => await Pipe.Output.WriteAsync(data).ConfigureAwait(false);

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

    public async ValueTask<IRemoteChannelWriter<T>> GetChannelWriterAsync<T>()
        => new ChannelWriter<T>(await Pipe.GetChannelWriter<T>().ConfigureAwait(false));

    public async ValueTask<IRemoteChannelReader<T>> GetChannelReaderAsync<T>()
        => new ChannelReader<T>(await Pipe.GetChannelReader<T>().ConfigureAwait(false));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await Pipe.DisposeAsync().ConfigureAwait(false);
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

        public async ValueTask<IReadOnlyList<T>> ReadBatchAsync(int maximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
            if (_buffered.Count == 0 && !_complete)
            {
                var list = new List<T>();
                // Returns false once the channel is complete and drained.
                if (!await reader.ReadAsync<T>(list, null).ConfigureAwait(false))
                    _complete = true;
                foreach (var item in list)
                    _buffered.Enqueue(item);
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
