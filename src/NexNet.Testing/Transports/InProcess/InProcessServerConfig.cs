using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Testing.Transports.InProcess;

/// <summary>
/// Server-side configuration for the in-process transport. The endpoint name is the rendezvous
/// key clients use to find this server; tests typically supply a unique value per test method.
/// </summary>
public sealed class InProcessServerConfig : ServerConfig
{
    /// <summary>
    /// Rendezvous key used to locate this server from a matching <see cref="InProcessClientConfig"/>.
    /// Must be unique per concurrently-running server within the process.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Creates a new in-process server configuration.
    /// </summary>
    public InProcessServerConfig()
        : base(ServerConnectionMode.Listener)
    {
    }

    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        var listener = new InProcessTransportListener(Endpoint);
        InProcessRendezvous.Register(Endpoint, listener);
        return new ValueTask<ITransportListener?>(listener);
    }
}
