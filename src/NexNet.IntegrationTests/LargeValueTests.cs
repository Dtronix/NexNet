using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Arguments and results larger than one frame (docs/internals/flow-control-v2.md, section 4.3).
/// </summary>
internal class LargeValueTests : BaseTests
{
    private static IEnumerable<TestCaseData> TransportsAndSizes()
    {
        foreach (var type in new[] { Type.Quic, Type.Uds, Type.Tcp, Type.TcpTls, Type.WebSocket, Type.HttpSocket })
        {
            foreach (var size in new[] { 128 * 1024, 1024 * 1024, 4 * 1024 * 1024 - 1024 })
                yield return new TestCaseData(type, size);
        }
    }

    [TestCaseSource(nameof(TransportsAndSizes))]
    public async Task LargeArgumentAndResult_RoundTrip(Type type, int size)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        var data = new byte[size];
        Random.Shared.NextBytes(data);

        var echoed = await client.Proxy.ServerEcho(data).Timeout(10);
        Assert.That(echoed, Is.EqualTo(data));

        // The session is still usable, and nothing stays reserved for the streams.
        Assert.That(await client.Proxy.ServerDataResult(10).Timeout(1), Has.Length.EqualTo(10));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task FireAndForgetLargeArgument_IsDelivered(Type type)
    {
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(type), nexus => nexus.ServerDataEvent = (_, data) =>
        {
            received.TrySetResult(data.Length);
            return default;
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        await client.Proxy.ServerData(new byte[1024 * 1024]).Timeout(5);
        Assert.That(await received.Task.Timeout(5), Is.EqualTo(1024 * 1024));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task BroadcastLargeArgument_ReachesEveryClient(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), null);
        await server.StartAsync().Timeout(1);

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
            await client.ConnectAsync().Timeout(1);
        }

        using var context = server.ContextProvider.Rent();
        await context.Proxy.All.ClientData(new byte[300 * 1024]).Timeout(5);

        foreach (var tcs in received)
            Assert.That(await tcs.Task.Timeout(5), Is.EqualTo(300 * 1024));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
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
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        var first = client.Proxy.ServerEcho(new byte[900 * 1024]).AsTask();
        await holding.Task.Timeout(5);

        await Assert.ThatAsync(async () => await client.Proxy.ServerEcho(new byte[900 * 1024]).AsTask().Timeout(5),
            Throws.InstanceOf<NexusBusyException>());

        // The session is fine; once the first call ends its reservation is released.
        Assert.That(await client.Proxy.ServerEcho(new byte[10]).Timeout(1), Has.Length.EqualTo(10));
        hold.SetResult();
        Assert.That(await first.Timeout(5), Has.Length.EqualTo(900 * 1024));
        Assert.That(await client.Proxy.ServerEcho(new byte[900 * 1024]).Timeout(5), Has.Length.EqualTo(900 * 1024));
    }

    [Test]
    public void ConfigResolvesTheElementLimitFromMaxMessageSize()
    {
        var config = CreateServerConfig(Type.Tcp);
        Assert.That(config.SerializerOptions.MaxElements, Is.EqualTo(4 * 1024 * 1024 / 16));

        config.MaxMessageSize = 64 * 1024;
        Assert.That(config.SerializerOptions.MaxElements, Is.EqualTo(65536));

        // An explicit limit is kept.
        config.SerializerOptions = NexNet.Serialization.NexusSerializerOptions.Untrusted with { MaxElements = 10 };
        Assert.That(config.SerializerOptions.MaxElements, Is.EqualTo(10));
    }
}
