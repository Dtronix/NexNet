using System.Security.Cryptography;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Arguments and results larger than one frame (docs/internals/flow-control.md, section 4.3), sent and received by
/// a client under test (P01b). Values are seeded patterns compared by digest.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class LargeValueTests(LanguageHostPool hosts) : InvocationMatrixTests(hosts)
{
    private static readonly int[] Sizes = [128 * 1024, 1024 * 1024, 4 * 1024 * 1024 - 1024];

    private static string Digest(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Test]
    [ClientMatrix<int>(nameof(Sizes), Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket])]
    public async Task LargeArgumentAndResult_RoundTrip(ClientTarget target, int size)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        var data = PipePattern.Create(size, size);
        var echoed = await Proxy(client).ServerEcho(data).Timeout(T(10));
        await Assert.That(echoed.Length).IsEqualTo(size);
        await Assert.That(Digest(echoed)).IsEqualTo(Digest(data));

        // The session is still usable, and nothing stays reserved for the streams.
        await Assert.That(await Proxy(client).ServerDataResult(10).Timeout(T(1))).Count().IsEqualTo(10);
        await DisconnectAndAudit(client);
    }

    /// <summary>C#-only QUIC companion of <see cref="LargeArgumentAndResult_RoundTrip"/> (no QUIC interop endpoint yet; H16).</summary>
    [Test]
    [Arguments(128 * 1024)]
    [Arguments(1024 * 1024)]
    [Arguments(4 * 1024 * 1024 - 1024)]
    public async Task LargeArgumentAndResult_RoundTrip_Quic(int size)
    {
        var server = CreateServer(CreateServerConfig(Type.Quic), null);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var (client, _) = CreateClient(CreateClientConfig(Type.Quic));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        var data = PipePattern.Create(size, size);
        var echoed = await client.Proxy.ServerEcho(data).Timeout(T(10));
        await Assert.That(Digest(echoed)).IsEqualTo(Digest(data));
        await Assert.That(await client.Proxy.ServerDataResult(10).Timeout(T(1))).Count().IsEqualTo(10);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task FireAndForgetLargeArgument_IsDelivered(ClientTarget target)
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(nexus => nexus.ServerDataEvent = (_, data) =>
        {
            received.TrySetResult(Digest(data));
            return default;
        });
        await using var client = await Connect(target, server);

        var data = PipePattern.Create(1024 * 1024, 7);
        await Proxy(client).ServerData(data).Timeout(T(5));
        await Assert.That(await received.Task.Timeout(T(5))).IsEqualTo(Digest(data));
        await DisconnectAndAudit(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task BroadcastLargeArgument_ReachesEveryClient(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var first = await Connect(target, server);
        await using var second = await Connect(target, server);

        var data = PipePattern.Create(300 * 1024, 11);
        using (var context = server.Server.ContextProvider.Rent())
            await context.Proxy.All.ClientData(data).Timeout(T(5));

        foreach (var client in new[] { first, second })
        {
            var call = await Script(client).ClientData.NextCallAsync().AsTask().Timeout(T(5));
            await Assert.That(Digest(call.data)).IsEqualTo(Digest(data));
        }

        await DisconnectAndAudit(first);
        await DisconnectAndAudit(second);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
    public async Task LargeArgumentWithoutBudget_ThrowsBusyAndTheSessionStaysUp(ClientTarget target)
    {
        // 1.5 MiB budget; 4 credits reserve 256 KiB of it. A held 900 KiB argument leaves no room for a second.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServer(
            nexus => nexus.ServerEchoEvent = async (_, data) =>
            {
                if (data.Length > 1024)
                {
                    holding.TrySetResult();
                    await hold.Task;
                }

                return data;
            },
            config =>
            {
                config.MaxMessageSize = 1024 * 1024;
                config.InvocationCredits = 4;
                config.MaxPipes = 1;
                config.PipeWindow = 64 * 1024;
                config.SessionBudget = 1536 * 1024;
            });
        await using var client = await Connect(target, server);
        var proxy = Proxy(client);

        var first = proxy.ServerEcho(new byte[900 * 1024]).AsTask();
        await holding.Task.Timeout(T(5));

        await Assert.ThrowsAsync<NexusBusyException>(async () => await proxy.ServerEcho(new byte[900 * 1024]).AsTask().Timeout(T(5)));

        // The session is fine; once the first call ends its reservation is released.
        await Assert.That(await proxy.ServerEcho(new byte[10]).Timeout(T(5))).Count().IsEqualTo(10);
        hold.SetResult();
        await Assert.That(await first.Timeout(T(5))).Count().IsEqualTo(900 * 1024);
        await Assert.That(await proxy.ServerEcho(new byte[900 * 1024]).Timeout(T(5))).Count().IsEqualTo(900 * 1024);
        await DisconnectAndAudit(client);
    }

    /// <summary>C#-only: the server configuration's element limit; no client is involved.</summary>
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