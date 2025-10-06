using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Pipes.Channels;

/// <summary>
/// Extension methods for the <see cref="INexusChannelReader{T}"/> interface.
/// </summary>
public static class NexusChannelReaderExtensions
{
    /// <summary>
    /// Asynchronously reads data from the duplex pipe.
    /// </summary>
    /// <param name="reader">The Nexus channel reader from which to read the items.</param>
    /// <param name="cancellationToken">An optional token to cancel the read operation.</param>
    /// <returns>
    /// A task that represents the asynchronous read operation. The value of the TResult parameter contains an enumerable collection of type T.
    /// If the read operation is completed or canceled, the returned task will contain an empty collection.
    /// </returns>
    public static async ValueTask<List<T>> ReadAsync<T>(
        this INexusChannelReader<T> reader,
        CancellationToken cancellationToken = default)
    {
        var list = new List<T>();
        await reader.ReadAsync(list, null, cancellationToken).ConfigureAwait(false);
        return list;
    }
}
