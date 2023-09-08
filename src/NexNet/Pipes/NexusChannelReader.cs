using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;

namespace NexNet.Pipes;

/// <summary>
/// Represents a structure for reading unmanaged data from a duplex pipe in the Nexus system.
/// </summary>
/// <typeparam name="T">The type of unmanaged data to be read from the duplex pipe. This type parameter is contravariant.</typeparam>
/// <remarks>
/// This structure provides asynchronous methods for reading data from the duplex pipe and converting it into an enumerable collection of type T.
/// It uses a <see cref="INexusDuplexPipe"/> for reading data.
/// </remarks>
internal class NexusChannelReader<T> : INexusChannelReader<T>
{
    internal readonly NexusPipeReader Reader;
    internal List<T>? List;

    /// <inheritdoc/>
    public bool IsComplete { get; protected set; }

    /// <inheritdoc/>
    public long BufferedLength => Reader.BufferedLength;

    /// <summary>
    /// Initializes a new instance of the <see cref="NexusChannelReaderUnmanaged{T}"/> class using the specified <see cref="INexusDuplexPipe"/>.
    /// </summary>
    /// <param name="pipe">The duplex pipe used for reading data.</param>
    public NexusChannelReader(INexusDuplexPipe pipe)
    : this(pipe.ReaderCore)
    {

    }

    internal NexusChannelReader(NexusPipeReader reader)
    {
        Reader = reader;

        // TODO: Review changing this out to another collection so that reallocation does not occur.
        List = new List<T>();
    }

    /// <inheritdoc/>
    public virtual async ValueTask<IEnumerable<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if(IsComplete)
            return Enumerable.Empty<T>();

        List?.Clear();

        // Read the data from the pipe reader.
        while (true)
        {
            var result = await Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            // Check if the result is completed or canceled.
            if (result.IsCompleted && Reader.BufferedLength == 0)
            {
                IsComplete = true;
                return Enumerable.Empty<T>();
            }

            if (result.IsCanceled)
                return Enumerable.Empty<T>();

            Read(result.Buffer, Reader, List!);

            if(List!.Count == 0)
                continue;

            return List!;
        }
    }

    /// <summary>
    /// Reads data from the buffer and converts it into an enumerable collection of type T.
    /// </summary>
    /// <param name="buffer">The buffer containing the data to be read.</param>
    /// <param name="pipeReader">The pipe reader used to advance the buffer after reading.</param>
    /// <param name="list">The list used to store the data.</param>
    /// <returns>An enumerable collection of type T.</returns>
    private static void Read(ReadOnlySequence<byte> buffer, NexusPipeReader pipeReader, List<T> list)
    {
        var length = buffer.Length;
        
        using var readerState = MemoryPackReaderOptionalStatePool.Rent(MemoryPackSerializerOptions.Default);
        using var reader = new MemoryPackReader(buffer, readerState);
        int successfulConsumedCount = 0;
        while ((length - reader.Consumed) > 0)
        {
            try
            {
                list.Add(reader.ReadValue<T>()!);
                successfulConsumedCount = reader.Consumed;
            }
            catch
            {
                break;
            }
        }

        if (successfulConsumedCount > 0)
        {
            pipeReader.AdvanceTo(successfulConsumedCount);
        }
    }

    /// <summary>
    /// Releases all resources used by the instance.
    /// </summary>
    public void Dispose()
    {
        var list = Interlocked.Exchange(ref List, null);
        if (list == null)
            return;

        list.Clear();
        list.TrimExcess();
    }
}
