using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Interface for accepting an external transport connection used by servers
/// </summary>
public interface IAcceptsExternalTransport
{
    /// <summary>
    /// Accept an already connected ITransport for a connection.
    /// </summary>
    /// <param name="transport">Transport to accept.</param>
    /// <param name="cancellationToken">Cancellation token to invoke upon transport completion.</param>
    /// <returns>Talk that completed upon the Client's completion.  Could be successful closure or exception.</returns>
    public ValueTask AcceptTransport(ITransport transport, CancellationToken cancellationToken = default);
}

/// <summary>
/// One external endpoint of a server (see <c>NexusServer.AddExternalEndpoint</c>), as used by NexNet.Asp.
/// </summary>
internal interface INexusExternalEndpoint : IAcceptsExternalTransport
{
    /// <summary>
    /// The endpoint's description; sessions see it as <c>Context.Endpoint</c>.
    /// </summary>
    NexusEndpointInfo Info { get; }

    /// <summary>
    /// The configuration whose session settings apply to the endpoint's sessions.
    /// </summary>
    ServerConfig Config { get; }

    /// <summary>
    /// Accepts a connected transport like <see cref="IAcceptsExternalTransport.AcceptTransport"/>, telling the
    /// session whether the connection is encrypted (for ASP.NET, <c>HttpRequest.IsHttps</c>).
    /// </summary>
    ValueTask AcceptTransport(ITransport transport, bool isSecure, CancellationToken cancellationToken = default);
}
