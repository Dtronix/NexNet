using System.IO.Pipelines;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Base transport for connections.
/// </summary>
public interface ITransport : IDuplexPipe
{
    /// <summary>
    /// Closes the connection.
    /// </summary>
    /// <param name="linger">
    /// Set to true to let the connection linger to allow for sending of last second packets.
    /// False if the connection is to close as soon as possible and possibly cut off any packets in the queue.
    /// </param>
    public ValueTask Close(bool linger);
}
