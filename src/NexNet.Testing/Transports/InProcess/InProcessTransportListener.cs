using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexNet.Transports;

namespace NexNet.Testing.Transports.InProcess;

/// <summary>
/// Listener that pairs incoming in-process client connections with the running server.
/// Producers (clients calling <see cref="ConnectAsClient"/>) build a paired transport and
/// enqueue the server-side half; consumers (the NexNet server) dequeue via
/// <see cref="AcceptTransportAsync"/>.
/// </summary>
internal sealed class InProcessTransportListener : ITransportListener
{
    private readonly string _endpoint;
    private readonly Channel<ITransport> _accepted;
    private int _connectionSequence;
    private bool _closed;

    public InProcessTransportListener(string endpoint)
    {
        _endpoint = endpoint;
        // Unbounded so clients never block on connect; the server's accept loop drains.
        _accepted = Channel.CreateUnbounded<ITransport>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Constructs a transport pair (server-side and client-side), enqueues the server-side for
    /// the server's accept loop, and returns the client-side to the connecting client.
    /// </summary>
    public ITransport ConnectAsClient()
    {
        if (_closed)
            throw new InvalidOperationException(
                $"InProcess listener '{_endpoint}' is closed; cannot connect.");

        // Two pipes, cross-wired: clientToServer carries client writes -> server reads;
        // serverToClient carries server writes -> client reads.
        var clientToServer = new Pipe(PipeOptions.Default);
        var serverToClient = new Pipe(PipeOptions.Default);

        var connectionId = Interlocked.Increment(ref _connectionSequence);
        var serverAddress = $"inproc://{_endpoint}/server#{connectionId}";
        var clientAddress = $"inproc://{_endpoint}/client#{connectionId}";

        var serverSide = new InProcessTransport(
            input: clientToServer.Reader,
            output: serverToClient.Writer,
            remoteAddress: clientAddress);

        var clientSide = new InProcessTransport(
            input: serverToClient.Reader,
            output: clientToServer.Writer,
            remoteAddress: serverAddress);

        if (!_accepted.Writer.TryWrite(serverSide))
            throw new InvalidOperationException(
                $"Failed to enqueue server-side transport on listener '{_endpoint}'.");

        return clientSide;
    }

    /// <inheritdoc />
    public async ValueTask<ITransport?> AcceptTransportAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask CloseAsync(bool linger)
    {
        if (_closed)
            return ValueTask.CompletedTask;
        _closed = true;

        _accepted.Writer.TryComplete();
        InProcessRendezvous.Unregister(_endpoint, this);
        return ValueTask.CompletedTask;
    }
}
