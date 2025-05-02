using System.IO.Pipelines;
using System.Net.Quic;
using NexNet.Transports;

namespace NexNet.IntegrationTests;


/// <summary>
/// Configurations for the server to allow connections from QUIC NexNet clients.
/// </summary>
public class CustomServerConfig : ServerConfig
{
    public CustomServerConfig(ServerConnectionMode connectionMode) : base(connectionMode)
    {
    }

    /// <param name="cancellationToken"></param>
    /// <inheritdoc />
    protected override ValueTask<ITransportListener?> OnCreateServerListener(CancellationToken cancellationToken)
    {
        return new ValueTask<ITransportListener?>((ITransportListener?)null);
    }
}
