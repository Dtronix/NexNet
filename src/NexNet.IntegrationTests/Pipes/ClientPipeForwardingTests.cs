using System.IO.Pipelines;
using System.Threading.Channels;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// The client runtime's pipe forward (C# <c>ForwardToAsync</c>, Java and JS <c>forwardTo</c>) on every client language:
/// the client under test opens pipes A and B to the server and forwards A to B (<see cref="RemotePipeExtensions.ForwardToAsync"/>,
/// control protocol <c>pipe.forward</c>); the server writes into A and reads B. <see cref="PipeForwardingTests"/> covers
/// the C# forward on the server's ends.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientPipeForwardingTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    /// <summary>The server's receive window of each pipe, small so that a few windows of data are quick to send.</summary>
    private const int Window = 64 * 1024;

    private readonly Channel<INexusDuplexPipe> _serverPipes = Channel.CreateUnbounded<INexusDuplexPipe>();

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ForwardTo_RelaysEveryByteInOrderThenHalfClosesTheDestination(ClientTarget target)
    {
        const int total = 16 * Window;
        await using var server = await StartRelayServerAsync();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        var forward = a.ForwardToAsync(b).AsTask();
        var writing = Task.Run(async () =>
        {
            await serverA.Output.WriteAsync(PipePattern.Create(total, 3));
            await serverA.Output.CompleteAsync();
        });

        // The server reads every byte from B in order, then sees B's input complete: the forward half-closed B.
        var received = await ReadToEndAsync(serverB.Input, 10);
        await Assert.That(received.Length).IsEqualTo(total);
        await Assert.That(received.AsSpan().SequenceEqual(PipePattern.Create(total, 3))).IsTrue();
        await writing.Timeout(T(5));
        await forward.Timeout(T(5));

        // Half-close: B's other direction and A's writing side are still open.
        await serverB.Output.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That((await b.ReadAsync(Data.Length, Data.Length).AsTask().Timeout(T(1))).ToArray())
            .IsEquivalentTo(Data, CollectionOrdering.Matching);
        await a.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverA.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ForwardTo_ForwardsBytesAPipeReadLeftUnreadFirst(ClientTarget target)
    {
        await using var server = await StartRelayServerAsync();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        // The client reads the first byte of A itself; the rest of the write may sit in the reader's buffer.
        var data = PipePattern.Create(1000, 9);
        await serverA.Output.WriteAsync(data).AsTask().Timeout(T(1));
        await Assert.That((await a.ReadAsync(1, 1).AsTask().Timeout(T(1))).ToArray()).IsEquivalentTo(data[..1], CollectionOrdering.Matching);

        var forward = a.ForwardToAsync(b).AsTask();
        await serverA.Output.CompleteAsync().AsTask().Timeout(T(1));
        await Assert.That(await ReadToEndAsync(serverB.Input)).IsEquivalentTo(data[1..], CollectionOrdering.Matching);
        await forward.Timeout(T(5));

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ForwardTo_DestinationCompletedWhileWaitingForCredit_ReturnsAndLeavesTheSourceOpen(ClientTarget target)
    {
        const int total = 16 * Window;
        await using var server = await StartRelayServerAsync();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        var forward = a.ForwardToAsync(b).AsTask();
        var writing = Task.Run(async () => await serverA.Output.WriteAsync(PipePattern.Create(total, 5)));

        // The server reads nothing from B: once B's window is full the forward waits for credit, and A's writer waits.
        var full = await serverB.Input.ReadAtLeastAsync(Window).AsTask().Timeout(T(5));
        serverB.Input.AdvanceTo(full.Buffer.Start, full.Buffer.End);
        await Task.Delay(100);
        await Assert.That(forward.IsCompleted).IsFalse();

        // The server stops reading B: the forward ends normally and leaves A as it is.
        await serverB.Input.CompleteAsync().AsTask().Timeout(T(1));
        await forward.Timeout(T(5));
        await Assert.That(writing.IsCompleted).IsFalse().Because("A was not read further, so its writer still waits.");
        await a.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverA.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);

        await serverA.CompleteAsync().AsTask().Timeout(T(1));
        await writing.Timeout(T(5));
        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ForwardTo_SessionEndsWhileForwarding_TheForwardEnds(ClientTarget target)
    {
        const int total = 16 * Window;
        await using var server = await StartRelayServerAsync();
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        var forward = a.ForwardToAsync(b).AsTask();
        var writing = Task.Run(async () => await serverA.Output.WriteAsync(PipePattern.Create(total, 7)));
        var full = await serverB.Input.ReadAtLeastAsync(Window).AsTask().Timeout(T(5));
        serverB.Input.AdvanceTo(full.Buffer.Start, full.Buffer.End);

        // The server ends the session while the forward waits for B's credit: both pipes end, and so does the forward.
        await sNexus.Context.DisconnectAsync().Timeout(T(1));
        await forward.Timeout(T(5));
        await writing.Timeout(T(5));

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ForwardTo_Canceled_StopsAndCompletesNeitherPipe(ClientTarget target)
    {
        await using var server = await StartRelayServerAsync();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        using var cts = new CancellationTokenSource();
        var forward = a.ForwardToAsync(b, cts.Token).AsTask();
        await serverA.Output.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverB.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);

        // Canceled while it waits for data: the forward fails canceled.
        await cts.CancelAsync();
        await AssertThrows<TaskCanceledException>(() => forward).Timeout(T(5));

        // It stopped: what the server writes into A now stays in A for the client to read, and B gets none of it.
        var later = PipePattern.Create(100, 11);
        await serverA.Output.WriteAsync(later).AsTask().Timeout(T(1));
        await Assert.That((await a.ReadAsync(later.Length, later.Length).AsTask().Timeout(T(1))).ToArray())
            .IsEquivalentTo(later, CollectionOrdering.Matching);
        await Assert.That(serverB.Input.TryRead(out _)).IsFalse().Because("the canceled forward wrote nothing more to B.");

        // Neither pipe is completed: both directions of A and B still carry data.
        await a.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverA.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);
        await b.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverB.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);
        await serverB.Output.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That((await b.ReadAsync(Data.Length, Data.Length).AsTask().Timeout(T(1))).ToArray())
            .IsEquivalentTo(Data, CollectionOrdering.Matching);

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ForwardTo_CanceledWhileWaitingForCredit_LeavesWhatItDidNotSendInTheSource(ClientTarget target)
    {
        const int total = 16 * Window;
        await using var server = await StartRelayServerAsync();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);
        var data = PipePattern.Create(total, 13);

        using var cts = new CancellationTokenSource();
        var forward = a.ForwardToAsync(b, cts.Token).AsTask();
        var writing = Task.Run(async () =>
        {
            await serverA.Output.WriteAsync(data);
            await serverA.Output.CompleteAsync();
        });

        // The server reads nothing from B: once B's window is full the forward waits for credit.
        // Nothing is consumed or examined, so B grants no credit and a later read returns these bytes at once.
        var full = await serverB.Input.ReadAtLeastAsync(Window).AsTask().Timeout(T(5));
        serverB.Input.AdvanceTo(full.Buffer.Start);
        await Task.Delay(100);
        await Assert.That(forward.IsCompleted).IsFalse();

        await cts.CancelAsync();
        await AssertThrows<TaskCanceledException>(() => forward).Timeout(T(5));

        // Every byte went either to B (before the cancel) or stays in A: none is lost or sent twice.
        var digest = await a.DrainAsync().AsTask().Timeout(T(10));
        await writing.Timeout(T(5));
        var forwarded = total - (int)digest.Length;
        await Assert.That(forwarded).IsGreaterThan(0);
        await Assert.That(digest.Sha256).IsEqualTo(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data.AsSpan(forwarded))));
        var atB = await ReadExactAsync(serverB.Input, forwarded, 5);
        await Assert.That(atB.AsSpan().SequenceEqual(data.AsSpan(0, forwarded))).IsTrue();
        await Task.Delay(100);
        await Assert.That(serverB.Input.TryRead(out _)).IsFalse().Because("the canceled forward wrote nothing more to B.");

        // B's writing side is not completed.
        await b.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverB.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    /// <summary>
    /// A server whose <c>ServerTaskValueWithDuplexPipe</c> hands each pipe to <see cref="OpenAsync"/> and holds it until
    /// it completes.
    /// </summary>
    private Task<InteropServer<ServerNexus, ServerNexus.ClientProxy>> StartRelayServerAsync()
        => StartServerAsync(
            nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
            {
                _serverPipes.Writer.TryWrite(pipe);
                await pipe.CompleteTask;
            },
            config => config.PipeWindow = Window);

    /// <summary>Opens a pipe from the client to the server; returns the client's handle and the server's end.</summary>
    private async Task<(IRemotePipe Client, INexusDuplexPipe Server)> OpenAsync(IClientUnderTest client)
    {
        var pipe = await client.CreatePipeAsync();
        await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
        return (pipe, await _serverPipes.Reader.ReadAsync().AsTask().Timeout(T(1)));
    }
}
