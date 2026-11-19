using NexNet.Interop;
using NexNet.Interop.Hosts;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Invocations a client under test cannot complete because of the message size limits (P01b).
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal partial class NexusClientTests_InvalidInvocations(LanguageHostPool hosts) : InvocationMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public async Task ClientThrowsWhenArgumentTooLarge(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        // Larger than the server's MaxMessageSize (4 MiB by default): refused before anything is sent.
        var data = new byte[4 * 1024 * 1024 + 1];
        await AssertThrows<NexusMessageTooLargeException>(() => Proxy(client).ServerData(data).AsTask())
            .Timeout(T(5));

        await AssertState(client, ConnectionState.Connected);
        await DisconnectAndAudit(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls])]
    public async Task ResultOverMaxMessageSizeFailsCallAndNextCallSucceeds(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server);

        // Larger than the client accepts (4 MiB by default). It used to leave the session's send buffer dirty,
        // failing every later send while pings kept the session looking alive.
        await AssertThrows<NexNet.Invocation.ProxyRemoteInvocationException>(() => Proxy(client).ServerDataResult(5 * 1024 * 1024).AsTask())
            .Timeout(T(5));

        var result = await Proxy(client).ServerDataResult(10).Timeout(T(1));
        await Assert.That(result.Length).IsEqualTo(10);
        await DisconnectAndAudit(client);
    }

    // ----- C#-only QUIC companions: the interop server has no QUIC endpoint yet, so the C# client keeps its QUIC
    // coverage of these tests here until the client matrix gains C#-only rows (H16). -----

    [Test]
    [Transports(Type.Quic)]
    public async Task ClientThrowsWhenArgumentTooLarge_Quic(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Larger than the server's MaxMessageSize (4 MiB by default): refused before anything is sent.
        var data = new byte[4 * 1024 * 1024 + 1];
        await AssertThrows<NexusMessageTooLargeException>(() => clientNexus.Context.Proxy.ServerData(data).AsTask())
            .Timeout(T(1));
    }

    [Test]
    [Transports(Type.Quic)]
    public async Task ResultOverMaxMessageSizeFailsCallAndNextCallSucceeds_Quic(Type type)
    {
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // Larger than the client accepts (4 MiB by default). It used to leave the session's send buffer dirty,
        // failing every later send while pings kept the session looking alive.
        await AssertThrows<NexNet.Invocation.ProxyRemoteInvocationException>(() => clientNexus.Context.Proxy.ServerDataResult(5 * 1024 * 1024).AsTask())
            .Timeout(T(5));

        var result = await clientNexus.Context.Proxy.ServerDataResult(10).Timeout(T(1));
        await Assert.That(result.Length).IsEqualTo(10);
    }
}
