using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// What a client reports about its connection, on the client matrix: whether its transport is secure (<c>IsSecure</c>)
/// and the server's address and port (<c>Context.RemoteAddress</c>/<c>RemotePort</c>). Ports the C#-only
/// <c>IsSecure_ReflectsTransport</c> and <c>RemoteEndpointTests.Client_*</c> tests to every language (Q07).
/// </summary>
/// <remarks>
/// <para>
/// Every client reports the peer of its socket as the remote endpoint, on every transport (<see cref="ExpectedEndpoint"/>).
/// The one exception is the browser: it cannot see the peer of its WebSocket, so its rows expect nothing (a platform
/// limit, like the server certificate).
/// </para>
/// <para>QUIC is a C#-only row (<see cref="ClientMatrixAttribute.CSharpTransports"/>).</para>
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientSessionInfoTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(
        Transports = [ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.Uds, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls],
        CSharpTransports = [ClientTransport.Quic])]
    public async Task IsSecure_ReflectsTransport(ClientTarget target)
    {
        // TLS, HTTPS, WSS and QUIC encrypt; a Unix domain socket never leaves the machine and counts as secure too.
        var expected = target.Transport.IsSecure() || target.Transport == ClientTransport.Uds;
        await using var server = await StartServerAsync();
        await using var client = await CreateClientAsync(target, server);

        await Assert.That(await client.GetIsSecureAsync()).IsEqualTo(expected).Because("isSecure is known before the client connects");

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await client.GetIsSecureAsync()).IsEqualTo(expected);

        await EndAsync(client);
        await Assert.That(await client.GetIsSecureAsync()).IsEqualTo(expected).Because("isSecure does not change on disconnect");
    }

    [Test]
    [ClientMatrix(
        Transports = [ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.Uds, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls],
        CSharpTransports = [ClientTransport.Quic])]
    public async Task RemoteEndpoint_IsTheServerEndpointWhileConnected(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await CreateClientAsync(target, server);

        await Assert.That(await client.GetRemoteEndpointAsync()).IsEqualTo(RemoteEndpoint.None).Because("not connected yet");

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await client.GetRemoteEndpointAsync()).IsEqualTo(ExpectedEndpoint(target, server.Endpoints));

        await EndAsync(client);
        await Assert.That(await client.GetRemoteEndpointAsync()).IsEqualTo(RemoteEndpoint.None).Because("disconnected");
    }

    /// <summary>What the client of <paramref name="target"/> reports as the server's endpoint once connected.</summary>
    private static RemoteEndpoint ExpectedEndpoint(ClientTarget target, InteropEndpoints endpoints)
    {
        // The browser does not expose the peer of a WebSocket.
        if (target.Language.StartsWith("browser-", StringComparison.Ordinal))
            return RemoteEndpoint.None;

        // A Unix domain socket has no port; the address is the socket path.
        if (target.Transport == ClientTransport.Uds)
            return new RemoteEndpoint(endpoints.UdsPath, null);

        // Every other interop endpoint listens on 127.0.0.1.
        var port = target.Transport switch
        {
            ClientTransport.Tcp => endpoints.Tcp.Port,
            ClientTransport.TcpTls => endpoints.TcpTls.Port,
            ClientTransport.HttpSocket => endpoints.HttpSocket.Port,
            ClientTransport.HttpSocketTls => endpoints.HttpSocketTls.Port,
            ClientTransport.WebSocket => endpoints.WebSocket.Port,
            ClientTransport.WebSocketTls => endpoints.WebSocketTls.Port,
            ClientTransport.Quic => endpoints.Quic!.Port,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Transport, null),
        };
        return new RemoteEndpoint("127.0.0.1", port);
    }
}
