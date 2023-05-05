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
    public void Close(bool linger);

    /// <summary>
    /// Listens and accepts new transport connections.
    /// </summary>
    /// <returns></returns>
    public Task<ITransport?> AcceptTransportAsync();
}
