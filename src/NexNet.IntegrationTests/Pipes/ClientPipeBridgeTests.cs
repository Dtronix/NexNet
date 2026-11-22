using System.Threading.Channels;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// The client runtime's pipe bridge (C# <c>BridgeAsync</c>, Java and JS <c>NexusDuplexPipe.bridge</c>) on every client
/// language: the client under test opens pipes A and B to the server and bridges them
/// (<see cref="RemotePipeExtensions.BridgeAsync"/>, control protocol <c>pipe.bridge</c>); the server writes into and reads
/// from both. <see cref="PipeForwardingTests"/> covers the C# bridge on the server's ends.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientPipeBridgeTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    /// <summary>The server's receive window of each pipe, small so that a few windows of data are quick to send.</summary>
    private const int Window = 64 * 1024;

    private readonly Channel<(ServerNexus Nexus, INexusDuplexPipe Pipe)> _serverPipes =
        Channel.CreateUnbounded<(ServerNexus, INexusDuplexPipe)>();

    /// <summary>
    /// Ends the server's pipe methods. A method that returns frees its pipe and drops what the test has not read yet,
    /// so the server holds each pipe until the test ends (<see cref="HoldServerPipes"/>), not only until it completes.
    /// </summary>
    private readonly TaskCompletionSource _release = Signal();

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Bridge_RelaysBothDirectionsAndReturnsOnceBothHalfClosed(ClientTarget target)
    {
        const int total = 8 * Window;
        await using var server = await StartRelayServerAsync();
        using var hold = HoldServerPipes();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        var bridge = a.BridgeAsync(b).AsTask();

        // A to B: the server reads every byte from B in order, then sees B's input complete (half-close).
        var writing = Task.Run(async () =>
        {
            await serverA.Output.WriteAsync(PipePattern.Create(total, 3));
            await serverA.Output.CompleteAsync();
        });
        var received = await ReadToEndAsync(serverB.Input, 10);
        await Assert.That(received.Length).IsEqualTo(total);
        await Assert.That(received.AsSpan().SequenceEqual(PipePattern.Create(total, 3))).IsTrue();
        await writing.Timeout(T(5));

        // B to A is still open, so the bridge keeps running.
        await Task.Delay(100);
        await Assert.That(bridge.IsCompleted).IsFalse();

        writing = Task.Run(async () =>
        {
            await serverB.Output.WriteAsync(PipePattern.Create(total, 4));
            await serverB.Output.CompleteAsync();
        });
        received = await ReadToEndAsync(serverA.Input, 10);
        await Assert.That(received.Length).IsEqualTo(total);
        await Assert.That(received.AsSpan().SequenceEqual(PipePattern.Create(total, 4))).IsTrue();
        await writing.Timeout(T(5));

        await bridge.Timeout(T(5));
        await serverA.CompleteTask.Timeout(T(5));
        await serverB.CompleteTask.Timeout(T(5));

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Bridge_ForwardsBytesAPipeReadLeftUnreadFirst(ClientTarget target)
    {
        await using var server = await StartRelayServerAsync();
        using var hold = HoldServerPipes();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        // The client reads the first byte of each pipe itself; the rest of the writes may sit in the readers' buffers.
        var toB = PipePattern.Create(1000, 9);
        var toA = PipePattern.Create(1000, 10);
        await serverA.Output.WriteAsync(toB).AsTask().Timeout(T(1));
        await serverB.Output.WriteAsync(toA).AsTask().Timeout(T(1));
        await Assert.That((await a.ReadAsync(1, 1).AsTask().Timeout(T(1))).ToArray()).IsEquivalentTo(toB[..1], CollectionOrdering.Matching);
        await Assert.That((await b.ReadAsync(1, 1).AsTask().Timeout(T(1))).ToArray()).IsEquivalentTo(toA[..1], CollectionOrdering.Matching);

        var bridge = a.BridgeAsync(b).AsTask();
        await serverA.Output.CompleteAsync().AsTask().Timeout(T(1));
        await serverB.Output.CompleteAsync().AsTask().Timeout(T(1));
        await Assert.That(await ReadToEndAsync(serverB.Input)).IsEquivalentTo(toB[1..], CollectionOrdering.Matching);
        await Assert.That(await ReadToEndAsync(serverA.Input)).IsEquivalentTo(toA[1..], CollectionOrdering.Matching);
        await bridge.Timeout(T(5));

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Bridge_SessionEndsWhileBridging_TheBridgeEnds(ClientTarget target)
    {
        const int total = 16 * Window;
        await using var server = await StartRelayServerAsync();
        using var hold = HoldServerPipes();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA, session) = await OpenWithNexusAsync(client);
        var (b, serverB, _) = await OpenWithNexusAsync(client);

        var bridge = a.BridgeAsync(b).AsTask();
        var writing = Task.Run(async () => await serverA.Output.WriteAsync(PipePattern.Create(total, 7)));
        var full = await serverB.Input.ReadAtLeastAsync(Window).AsTask().Timeout(T(5));
        serverB.Input.AdvanceTo(full.Buffer.Start, full.Buffer.End);

        // The server ends the session while the bridge waits for B's credit: both pipes end, and so does the bridge.
        await session.Context.DisconnectAsync().Timeout(T(1));
        await bridge.Timeout(T(5));
        await writing.Timeout(T(5));

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Bridge_PipeCompletedWhileBridging_CompletesTheOtherPipe(ClientTarget target)
    {
        await using var server = await StartRelayServerAsync();
        using var hold = HoldServerPipes();
        await using var client = await ConnectAsync(target, server);
        var (a, serverA) = await OpenAsync(client);
        var (b, serverB) = await OpenAsync(client);

        var bridge = a.BridgeAsync(b).AsTask();
        await serverA.Output.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverB.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);
        await serverB.Output.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverA.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);

        // The server completes A in both directions while the bridge runs: an abnormal end, so the bridge completes B
        // in both directions too and returns.
        await serverA.CompleteAsync().AsTask().Timeout(T(1));
        await serverB.CompleteTask.Timeout(T(5));
        await bridge.Timeout(T(5));

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Bridge_SessionOfOnePipeEndsWhileBridging_CompletesTheOtherPipe(ClientTarget target)
    {
        await using var server = await StartRelayServerAsync();
        using var hold = HoldServerPipes();
        await using var first = await ConnectAsync(target, server);
        await using var second = await ConnectAsync(target, server);
        var (a, serverA, sessionA) = await OpenWithNexusAsync(first);
        var (b, serverB, _) = await OpenWithNexusAsync(second);

        var bridge = a.BridgeAsync(b).AsTask();
        await serverA.Output.WriteAsync(Data).AsTask().Timeout(T(1));
        await Assert.That(await ReadExactAsync(serverB.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);

        // The server ends A's session: the bridge completes B, on the other session, and returns.
        await sessionA.Context.DisconnectAsync().Timeout(T(1));
        await serverB.CompleteTask.Timeout(T(5));
        await bridge.Timeout(T(5));

        await a.DisposeAsync();
        await b.DisposeAsync();
        await EndAsync(first);
        await EndAsync(second);
    }

    /// <summary>
    /// A server whose <c>ServerTaskValueWithDuplexPipe</c> hands each pipe and its session's nexus to
    /// <see cref="OpenWithNexusAsync"/> and holds the pipe until it completes and the test released it.
    /// </summary>
    private Task<InteropServer<ServerNexus, ServerNexus.ClientProxy>> StartRelayServerAsync()
        => StartServerAsync(
            nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (n, pipe) =>
            {
                _serverPipes.Writer.TryWrite((n, pipe));
                await Task.WhenAll(pipe.CompleteTask, _release.Task);
            },
            config => config.PipeWindow = Window);

    /// <summary>
    /// Holds the server's pipes until the returned scope is disposed. Declared after the server, it is disposed before it.
    /// </summary>
    private Release HoldServerPipes() => new(_release);

    private sealed class Release(TaskCompletionSource release) : IDisposable
    {
        public void Dispose() => release.TrySetResult();
    }

    /// <summary>Opens a pipe from the client to the server; returns the client's handle and the server's end.</summary>
    private async Task<(IRemotePipe Client, INexusDuplexPipe Server)> OpenAsync(IClientUnderTest client)
    {
        var (pipe, server, _) = await OpenWithNexusAsync(client);
        return (pipe, server);
    }

    /// <summary>
    /// Opens a pipe from the client to the server; returns the client's handle, the server's end and the server nexus of
    /// the client's session.
    /// </summary>
    private async Task<(IRemotePipe Client, INexusDuplexPipe Server, ServerNexus Session)> OpenWithNexusAsync(IClientUnderTest client)
    {
        var pipe = await client.CreatePipeAsync();
        await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
        var (nexus, server) = await _serverPipes.Reader.ReadAsync().AsTask().Timeout(T(1));
        return (pipe, server, nexus);
    }
}
