using System;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NexNet.Transports;

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

    public IReconnectionPolicy? ReconnectionPolicy { get; set; } = new DefaultReconnectionPolicy();

    /// <summary>
    /// Method called to pass data to the server upon connection.  If not overridden,
    /// the client will not pass any authentication information to the server.
    /// </summary>
    public Func<byte[]?>? Authenticate { get; set; }

    internal Action? InternalOnClientConnect;

    public abstract ValueTask<ITransportBase> ConnectTransport();

}
