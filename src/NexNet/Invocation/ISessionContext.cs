using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using NexNet.Logging;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.Invocation;

/// <summary>
/// Base context for hubs to use.
/// </summary>
public interface ISessionContext
{
    /// <summary>
    /// Logger.
    /// </summary>
    INexusLogger? Logger { get; }

    /// <summary>
    /// Store for this session used to keep and pass variables for the lifetime of this session.
    /// </summary>
    SessionStore Store { get; }

    /// <summary>
    /// Id of the current session.
    /// </summary>
    long Id { get; }

    /// <summary>
    /// The certificate the remote peer presented during the TLS handshake. In a server nexus this is the client
    /// certificate; in a client nexus it is the server certificate. Null when the connection is not TLS or the
    /// peer presented no certificate.
    /// </summary>
    /// <remarks>
    /// Set for TCP TLS, QUIC, and WebSocket and HttpSocket over HTTPS. On an ASP server it is
    /// <c>HttpContext.Connection.ClientCertificate</c>: a certificate requested later with
    /// <c>GetClientCertificateAsync</c> (<c>ClientCertificateMode.DelayCertificate</c>) is not seen. It belongs to
    /// the connection and is valid while the session is connected; copy it to keep it longer.
    /// </remarks>
    X509Certificate2? RemoteCertificate { get; }

    /// <summary>
    /// On an ASP.NET server (WebSocket and HttpSocket), the principal of the HTTP request that opened the
    /// connection (<c>HttpContext.User</c>). Null for TCP, TLS, UDS and QUIC, and in a client nexus.
    /// </summary>
    /// <remarks>
    /// Captured once when the connection is accepted and never re-validated during the session. With
    /// <c>AspEnableAuthentication</c> it is the principal of <c>AspAuthenticationScheme</c>; otherwise whatever the
    /// pipeline set (for example <c>UseAuthentication</c> with a default scheme), which may be unauthenticated.
    /// Readable in <c>OnAuthenticate</c>: it is extra input there, not the session's identity, and
    /// <c>OnAuthenticate</c> still decides whether the connection is accepted.
    /// </remarks>
    ClaimsPrincipal? User { get; }

    /// <summary>
    /// Creates a pipe for use with the current session.
    /// </summary>
    /// <returns>Pipe for communication over teh current session.</returns>
    IRentedNexusDuplexPipe CreatePipe();

    /// <summary>
    /// Creates a duplex channel for the specified type.
    /// </summary>
    /// <typeparam name="T">The type of data to be transmitted through the channel.</typeparam>
    /// <returns>An instance of <see cref="INexusDuplexChannel{T}"/> that allows for bidirectional communication of data.</returns>
    /// <remarks>
    /// This method creates a channel from a rented pipe and is suitable for any MessagePack data type.
    /// </remarks>
    INexusDuplexChannel<T> CreateChannel<T>();

    /// <summary>
    /// Disconnect the current connection.
    /// </summary>
    Task DisconnectAsync();
    
    /// <summary>
    /// Disconnect the current connection.
    /// </summary>
    /// <param name="reason">Reason for the disconnection.</param>
    internal Task DisconnectAsync(DisconnectReason reason);

    /// <summary>
    /// Resets the context for closure.
    /// </summary>
    void Reset();
}
