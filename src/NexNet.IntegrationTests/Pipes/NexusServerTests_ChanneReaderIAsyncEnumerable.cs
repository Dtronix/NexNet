using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Channels on pipes the server opens to the client under test: the server's channel reader. The client echoes every
/// byte back (<c>pipe.echo</c>), so the server reads what it wrote, through the client.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusServerTests_ChanneReaderIAsyncEnumerable(LanguageHostPool hosts) : PipeMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task IAsyncEnumerable(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, s => s.ClientTaskValueWithDuplexPipe.Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            var counter = 0;
            var reading = Task.Run(async () =>
            {
                await foreach (var _ in await ChannelReader<ComplexMessage>(pipe))
                    Interlocked.Increment(ref counter);
            });

            var writer = await ChannelWriter<ComplexMessage>(pipe);
            for (var i = 0; i < 10; i++)
            {
                await writer.WriteAsync(ComplexMessage.Random()).AsTask().Timeout(T(1));
                await Task.Delay(1);
            }

            await writer.CompleteAsync().AsTask().Timeout(T(1));

            // The client completes the pipe after echoing everything, which ends the server's enumeration.
            await reading.Timeout(T(1));
            await Assert.That(Volatile.Read(ref counter)).IsEqualTo(10);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task StopsOnCompletion(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, s => s.ClientTaskValueWithDuplexPipe.Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            var writer = await ChannelWriter<int>(pipe);
            await writer.WriteAsync(1).AsTask().Timeout(T(1));
            await writer.CompleteAsync().AsTask().Timeout(T(1));

            var messageReceivedCount = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(T(1)));
            await foreach (var _ in (await ChannelReader<int>(pipe)).WithCancellation(cts.Token))
                messageReceivedCount++;

            await pipe.CompleteTask.Timeout(T(1));
            await Assert.That(messageReceivedCount).IsEqualTo(1);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task CancelsRead(ClientTarget target)
    {
        await using var server = await StartServerAsync();

        // The echo starts 200 ms after the call: the server's 50 ms read gets nothing.
        await using var client = await ConnectAsync(target, server,
            s => s.ClientTaskValueWithDuplexPipe.Delay(200).Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            var writer = await ChannelWriter<int>(pipe);
            await writer.WriteAsync(1).AsTask().Timeout(T(1));
            await writer.CompleteAsync().AsTask().Timeout(T(1));

            var messageReceivedCount = 0;
            using var cts = new CancellationTokenSource(50);
            await foreach (var _ in (await ChannelReader<int>(pipe)).WithCancellation(cts.Token))
                messageReceivedCount++;

            await Assert.That(messageReceivedCount).IsEqualTo(0);
            await pipe.CompleteTask.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task CancelsAndResumesRead(ClientTarget target)
    {
        await using var server = await StartServerAsync();

        // The echo starts 200 ms after the call: the server's 50 ms read gets nothing.
        await using var client = await ConnectAsync(target, server,
            s => s.ClientTaskValueWithDuplexPipe.Delay(200).Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            var writer = await ChannelWriter<int>(pipe);
            await writer.WriteAsync(1).AsTask().Timeout(T(1));
            await writer.CompleteAsync().AsTask().Timeout(T(1));

            var messageReceivedCount = 0;
            using var cts = new CancellationTokenSource(50);
            await foreach (var _ in (await ChannelReader<int>(pipe)).WithCancellation(cts.Token))
                messageReceivedCount++;

            await Assert.That(messageReceivedCount).IsEqualTo(0);

            await foreach (var _ in await ChannelReader<int>(pipe))
                messageReceivedCount++;

            await Assert.That(messageReceivedCount).IsEqualTo(1);
            await pipe.CompleteTask.Timeout(T(1));
        }

        await EndAsync(client);
    }
}
