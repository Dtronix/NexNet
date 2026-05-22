using System;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Testing.Transports.InProcess;

/// <summary>
/// Client-side configuration for the in-process transport. The endpoint must match the
/// <see cref="InProcessServerConfig.Endpoint"/> of a server that is already started in the
/// same process.
/// </summary>
public sealed class InProcessClientConfig : ClientConfig
{
    /// <summary>
    /// Rendezvous key identifying which in-process server to connect to.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <inheritdoc />
    protected override ValueTask<ITransport> OnConnectTransport(CancellationToken cancellationToken)
    {
        var listener = InProcessRendezvous.Find(Endpoint)
            ?? throw new TransportException(
                TransportError.ConnectionRefused,
                $"No InProcess listener is registered at endpoint '{Endpoint}'. " +
                "The server must be started before clients can connect.",
                null);

        return new ValueTask<ITransport>(listener.ConnectAsClient());
    }
}
