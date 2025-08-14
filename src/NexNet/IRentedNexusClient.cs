using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Invocation;
using NexNet.Transports;

namespace NexNet;

/// <summary>
/// Represents a rented client from a NexusClientPool that must be returned when finished.
/// </summary>
/// <typeparam name="TServerProxy">Server proxy implementation used for all remote invocations.</typeparam>
public interface IRentedNexusClient<out TServerProxy> : IAsyncDisposable, IDisposable
    where TServerProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash
{
    /// <summary>
    /// Proxy used for invoking remote methods on the server.
    /// </summary>
    TServerProxy Proxy { get; }

    /// <summary>
    /// Current state of the connection.
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// Ensures the client is connected, attempting to reconnect if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the connection attempt.</param>
    /// <returns>True if connected successfully, false otherwise.</returns>
    Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Task which completes upon the disconnection of the client.
    /// </summary>
    Task DisconnectedTask { get; }
}
