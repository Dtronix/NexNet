using System.IO.Pipelines;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
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
    /// The certificate the remote peer presented during the TLS handshake. On a server this is the client
    /// certificate; on a client it is the server certificate. Null when the connection is not TLS or the peer
    /// presented no certificate.
    /// </summary>
    /// <remarks>
    /// Belongs to the connection and is valid until the transport is closed. Copy it to keep it longer.
    /// </remarks>
    X509Certificate2? RemoteCertificate { get; }

    /// <summary>
    /// On a server, the ASP.NET principal (<c>HttpContext.User</c>) of the request that opened the connection,
    /// captured when the connection was accepted. Null for transports that are not accepted through ASP.NET
    /// (TCP, TLS, UDS, QUIC) and on clients.
    /// </summary>
    /// <remarks>
    /// Only the principal is kept, never the <c>HttpContext</c>. It is not re-validated during the connection.
    /// </remarks>
    ClaimsPrincipal? User => null;

    /// <summary>
    /// Closes the connection.
    /// </summary>
    /// <param name="linger">
    /// Set to true to let the connection linger to allow for sending of last second packets.
    /// False if the connection is to close as soon as possible and possibly cut off any packets in the queue.
    /// </param>
    public ValueTask CloseAsync(bool linger);
}
