using System.IO.Pipelines;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Base transport for connections.
/// </summary>
public interface ITransport : IDuplexPipe
{
    /// <summary>
    /// The remote address of the connected client.
    /// For IP-based transports, this is the IP address.
    /// For Unix Domain Sockets, this may be null or the socket path.
    /// When behind a proxy, this may be the original client IP from X-Forwarded-For.
    /// </summary>
    string? RemoteAddress { get; }

    /// <summary>
    /// The remote port of the connected client.
    /// Null for transports that don't use ports (e.g., Unix Domain Sockets).
    /// </summary>
    int? RemotePort { get; }

    /// <summary>
    /// Closes the connection.
    /// </summary>
    /// <param name="linger">
    /// Set to true to let the connection linger to allow for sending of last second packets.
    /// False if the connection is to close as soon as possible and possibly cut off any packets in the queue.
    /// </param>
    public ValueTask CloseAsync(bool linger);
}
