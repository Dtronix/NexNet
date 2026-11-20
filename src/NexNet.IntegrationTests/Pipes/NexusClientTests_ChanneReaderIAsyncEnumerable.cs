using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>Channels on pipes the client under test opens: the client's channel reader and writer.</summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusClientTests_ChanneReaderIAsyncEnumerable(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    private const string NoReadCancel = "a remote channel read cannot be canceled";

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task IAsyncEnumerable(ClientTarget target)
    {
        // The client writes strings (an item type every language host has), the server answers each with a counter.
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var writer = await ChannelWriter<int>(pipe);
            var counter = 0;
            await foreach (var message in await ChannelReader<string>(pipe))
                await writer.WriteAsync(counter++);
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));

            var reader = await pipe.GetChannelReaderAsync<int>();
            var reading = reader.ReadAllAsync().AsTask();
            var writer = await pipe.GetChannelWriterAsync<string>();
            for (var i = 0; i < 10; i++)
            {
                await writer.WriteAsync("message " + i).AsTask().Timeout(T(1));
                await Task.Delay(1);
            }

            await writer.CompleteAsync().AsTask().Timeout(T(1));
            await Assert.That(await reading.Timeout(T(1))).IsEquivalentTo(Enumerable.Range(0, 10), CollectionOrdering.Matching);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task StopsOnCompletion(ClientTarget target)
    {
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var writer = await ChannelWriter<int>(pipe);
            await writer.WriteAsync(1);
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));

            // The server's method returns after one item, which completes the channel.
            var reader = await pipe.GetChannelReaderAsync<int>();
            var items = await reader.ReadAllAsync().AsTask().Timeout(T(1));
            await pipe.WhenCompletedAsync().AsTask().Timeout(T(1));

            await Assert.That(items.Count).IsEqualTo(1);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task CancelsRead(ClientTarget target)
    {
        RequireInProc(target, NoReadCancel);
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var writer = await ChannelWriter<int>(pipe);
            await Task.Delay(200);
            await writer.WriteAsync(1);
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = InProc(client).Client.CreatePipe())
        {
            await InProc(client).Client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.ReadyTask.Timeout(T(1));
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
        RequireInProc(target, NoReadCancel);
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var writer = await ChannelWriter<int>(pipe);
            await Task.Delay(200);
            await writer.WriteAsync(1);
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = InProc(client).Client.CreatePipe())
        {
            await InProc(client).Client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.ReadyTask.Timeout(T(1));
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
