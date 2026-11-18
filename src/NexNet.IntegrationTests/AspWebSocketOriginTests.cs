using System.Net;
using System.Net.WebSockets;
using NexNet.Asp.WebSocket;
using NexNet.Transports;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests;

internal class AspWebSocketOriginTests : BaseAspTests
{
    private const string AllowedOrigin = "https://allowed.example";

    [Test]
    public async Task AllowedOriginConnects()
    {
        var url = await StartServerWithAllowedOrigin();

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", AllowedOrigin);
        await socket.ConnectAsync(url, CancellationToken.None).Timeout(1);

        await Assert.That(socket.State).IsEqualTo(WebSocketState.Open);
        socket.Abort();
    }

    [Test]
    public async Task AllowedOriginIsCaseInsensitive()
    {
        var url = await StartServerWithAllowedOrigin();

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", AllowedOrigin.ToUpperInvariant());
        await socket.ConnectAsync(url, CancellationToken.None).Timeout(1);

        await Assert.That(socket.State).IsEqualTo(WebSocketState.Open);
        socket.Abort();
    }

    [Test]
    public async Task BlockedOriginIsForbidden()
    {
        var url = await StartServerWithAllowedOrigin();

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", "https://evil.example");

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(url, CancellationToken.None).Timeout(1));
        await Assert.That(socket.HttpStatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ClientWithoutOriginConnects()
    {
        var clientConfig = CreateClientConfig(Type.WebSocket);
        var serverConfig = (WebSocketServerConfig)CreateServerConfig(Type.WebSocket);
        serverConfig.AllowedOrigins.Add(AllowedOrigin);

        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        await client.ConnectAsync().Timeout(1);
    }

    [Test]
    public async Task EmptyAllowedOriginsAllowsAnyOrigin()
    {
        var clientConfig = (HttpClientConfig)CreateClientConfig(Type.WebSocket);
        var serverConfig = (WebSocketServerConfig)CreateServerConfig(Type.WebSocket);
        await Assert.That(serverConfig.AllowedOrigins).IsEmpty();

        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://any.example");
        await socket.ConnectAsync(clientConfig.Url, CancellationToken.None).Timeout(1);

        await Assert.That(socket.State).IsEqualTo(WebSocketState.Open);
        socket.Abort();
    }

    private async Task<Uri> StartServerWithAllowedOrigin()
    {
        var clientConfig = (HttpClientConfig)CreateClientConfig(Type.WebSocket);
        var serverConfig = (WebSocketServerConfig)CreateServerConfig(Type.WebSocket);
        serverConfig.AllowedOrigins.Add(AllowedOrigin);

        var server = CreateServer(serverConfig, null);
        await server.StartAsync().Timeout(1);
        return clientConfig.Url;
    }
}