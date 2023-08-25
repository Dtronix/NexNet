using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Base for listening and accepting new connections.
/// </summary>
public interface ITransportListener
{
    /// <summary>
    /// Closes the listener.
    /// </summary>
    /// <param name="linger">
    /// Set to true to let the connection linger to allow for sending of last second packets.
    /// False if the connection is to close as soon as possible and possibly cut off any packets in the queue.
    /// </param>
    public ValueTask CloseAsync(bool linger);

    /// <summary>
    /// Listens and accepts new transport connections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>New transport connection.  Null if the listener is closed.</returns>
    public ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken);
}
