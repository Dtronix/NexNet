using NexNet.IntegrationTests.TestInterfaces;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Arguments and results larger than one frame (docs/internals/flow-control.md, section 4.3).
/// </summary>
internal class LargeValueTests : BaseTests
{
    public static IEnumerable<Func<(Type, int)>> TransportsAndSizes()
    {
        foreach (var type in new[] { Type.Quic, Type.Uds, Type.Tcp, Type.TcpTls, Type.WebSocket, Type.HttpSocket })
        {
            foreach (var size in new[] { 128 * 1024, 1024 * 1024, 4 * 1024 * 1024 - 1024 })
                yield return () => (type, size);
        }
    }

    [Test]
    [MethodDataSource(nameof(TransportsAndSizes))]
    public async Task LargeArgumentAndResult_RoundTrip(Type type, int size)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var data = new byte[size];
        Random.Shared.NextBytes(data);

        var echoed = await client.Proxy.ServerEcho(data).Timeout(T(10));
        await Assert.That(echoed).IsEquivalentTo(data, CollectionOrdering.Matching);

        // The session is still usable, and nothing stays reserved for the streams.
        await Assert.That(await client.Proxy.ServerDataResult(10).Timeout(T(1))).Count().IsEqualTo(10);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task FireAndForgetLargeArgument_IsDelivered(Type type)
    {
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(type), nexus => nexus.ServerDataEvent = (_, data) =>
        {
            received.TrySetResult(data.Length);
            return default;
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await client.Proxy.ServerData(new byte[1024 * 1024]).Timeout(T(5));
        await Assert.That(await received.Task.Timeout(T(5))).IsEqualTo(1024 * 1024);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task BroadcastLargeArgument_ReachesEveryClient(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var received = new[]
        {
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        foreach (var tcs in received)
        {
            var (client, clientNexus) = CreateClient(CreateClientConfig(type));
            clientNexus.ClientDataEvent = (_, data) =>
            {
                tcs.TrySetResult(data.Length);
                return default;
            };
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        }

        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientData(new byte[300 * 1024]).Timeout(T(5));

        foreach (var tcs in received)
            await Assert.That(await tcs.Task.Timeout(T(5))).IsEqualTo(300 * 1024);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task LargeArgumentWithoutBudget_ThrowsBusyAndTheSessionStaysUp(Type type)
    {
        // 1.5 MiB budget; 4 credits reserve 256 KiB of it. A held 900 KiB argument leaves no room for a second.
        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxMessageSize = 1024 * 1024;
        serverConfig.InvocationCredits = 4;
        serverConfig.MaxPipes = 1;
        serverConfig.PipeWindow = 64 * 1024;
        serverConfig.SessionBudget = 1536 * 1024;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerEchoEvent = async (_, data) =>
        {
            if (data.Length > 1024)
            {
                holding.TrySetResult();
                await hold.Task;
            }

            return data;
        });
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var first = client.Proxy.ServerEcho(new byte[900 * 1024]).AsTask();
        await holding.Task.Timeout(T(5));

        await Assert.ThrowsAsync<NexusBusyException>(async () => await client.Proxy.ServerEcho(new byte[900 * 1024]).AsTask().Timeout(T(5)));

        // The session is fine; once the first call ends its reservation is released.
        await Assert.That(await client.Proxy.ServerEcho(new byte[10]).Timeout(T(5))).Count().IsEqualTo(10);
        hold.SetResult();
        await Assert.That(await first.Timeout(T(5))).Count().IsEqualTo(900 * 1024);
        await Assert.That(await client.Proxy.ServerEcho(new byte[900 * 1024]).Timeout(T(5))).Count().IsEqualTo(900 * 1024);
    }

    [Test]
    public async Task ConfigResolvesTheElementLimitFromMaxMessageSize()
    {
        var config = CreateServerConfig(Type.Tcp);
        await Assert.That(config.SerializerOptions.MaxElements).IsEqualTo(4 * 1024 * 1024 / 16);

        config.MaxMessageSize = 64 * 1024;
        await Assert.That(config.SerializerOptions.MaxElements).IsEqualTo(65536);

        // An explicit limit is kept.
        config.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Untrusted with { MaxElements = 10 };
        await Assert.That(config.SerializerOptions.MaxElements).IsEqualTo(10);
    }
}