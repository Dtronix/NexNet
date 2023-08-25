using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// Base client configurations.
/// </summary>
public abstract class ClientConfig : ConfigBase
{
    /// <summary>
    /// Number of milliseconds before the connection cancels.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 50_000;

    /// <summary>
    /// Number of milliseconds between ping messages sent to the server.
    /// </summary>
    public int PingInterval { get; set; } = 10_000;

    /// <summary>
    /// Policy for reconnecting to the server upon connection closing.
    /// Set to null to disable this functionality.
    /// </summary>
    public IReconnectionPolicy? ReconnectionPolicy { get; set; } = null;

    /// <summary>
    /// Method called to pass data to the server upon connection.  If not overridden,
    /// the client will not pass any authentication information to the server.
    /// </summary>
    public Func<Memory<byte>>? Authenticate { get; set; }

    /// <summary>
    /// Returns the transport configured and connected for the overridden configurations.
    /// </summary>
    /// <returns>Connected transport.</returns>
    /// <param name="cancellationToken">Cancellation token used to cancel the connection.</param>
    /// <exception cref="TransportException">Throws socket exception upon failure to connect.</exception>
    internal ValueTask<ITransport> ConnectTransport(CancellationToken cancellationToken)
    {
        return OnConnectTransport(cancellationToken);
    }

    /// <summary>
    /// Override to return the transport configured and connected for the overridden configurations.
    /// </summary>
    /// <returns>Connected transport.</returns>
    /// <param name="cancellationToken">Cancellation token used to cancel the connection.</param>
    /// <exception cref="TransportException">Throws socket exception upon failure to connect.</exception>
    protected abstract ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken);

    internal Action? InternalOnClientConnect;

}
