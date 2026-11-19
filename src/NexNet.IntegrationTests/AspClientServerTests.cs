using System.Net.Http.Headers;
using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Transports;

namespace NexNet.IntegrationTests;

internal partial class AspClientServerTests : BaseAspTests
{
    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task ClientAuthenticationSucceeds(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);

        if (clientConfig is HttpClientConfig httpClientConfig)
        {
            httpClientConfig.AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "Token123");
        }
        
        if (serverConfig is AspServerConfig aspServerConfig)
        {
            aspServerConfig.AspEnableAuthentication = true;
            aspServerConfig.AspAuthenticationScheme = "BearerToken";
        }
        
        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);
        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await Assert.That(() => client.ConnectAsync().Timeout(TestTimeouts.Connect)).ThrowsNothing();
        await tcs.Task.Timeout(1);
    }
    
    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task ClientAuthenticationFailsWithBadAuth(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);

        if (clientConfig is HttpClientConfig httpClientConfig)
        {
            httpClientConfig.AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "Bad Token");
        }
        
        if (serverConfig is AspServerConfig aspServerConfig)
        {
            aspServerConfig.AspEnableAuthentication = true;
            aspServerConfig.AspAuthenticationScheme = "BearerToken";
        }
        
        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);
        
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        
        var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(TestTimeouts.Connect));
        await Assert.That(actual!.Error).IsEqualTo(TransportError.AuthenticationError);
    }
    
    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task ClientAuthenticationFailsWithNoAuth(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        
        if (serverConfig is AspServerConfig aspServerConfig)
        {
            aspServerConfig.AspEnableAuthentication = true;
            aspServerConfig.AspAuthenticationScheme = "BearerToken";
        }
        
        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);
        
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        
        var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(TestTimeouts.Connect));
        await Assert.That(actual!.Error).IsEqualTo(TransportError.AuthenticationError);
    }
    
    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task ServerIgnoresAuthenticationWhenNotEnabled(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);

        if (clientConfig is HttpClientConfig httpClientConfig)
        {
            httpClientConfig.AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "Bad Token");
        }
        
        if (serverConfig is AspServerConfig aspServerConfig)
        {
            aspServerConfig.AspEnableAuthentication = false;
        }
        
        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);
        
        await server.StartAsync().Timeout(TestTimeouts.Connect);
  
        await client.ConnectAsync().Timeout(TestTimeouts.Connect);
    }
    
    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task ClientDoesNotConnectWhenServerSelectsBadAuthScheme(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);

        if (clientConfig is HttpClientConfig httpClientConfig)
        {
            httpClientConfig.AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "Token123");
        }
        
        if (serverConfig is AspServerConfig aspServerConfig)
        {
            aspServerConfig.AspEnableAuthentication = true;
            aspServerConfig.AspAuthenticationScheme = "BadScheme";
        }
        
        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);
        
        await server.StartAsync().Timeout(TestTimeouts.Connect);
        
        var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(TestTimeouts.Connect));
        await Assert.That(actual!.Error).IsEqualTo(TransportError.InternalError);
    }

    [Test]
    [Arguments(Type.WebSocket, "Token123", true)]
    [Arguments(Type.HttpSocket, "Token123", true)]
    [Arguments(Type.WebSocket, "BadToken", false)]
    [Arguments(Type.HttpSocket, "BadToken", false)]
    public async Task QueryStringTokenAuthenticatesWhenEnabled(Type type, string token, bool succeeds)
    {
        var clientConfig = (HttpClientConfig)CreateClientConfig(type);
        var serverConfig = (AspServerConfig)CreateServerConfig(type);

        clientConfig.Url = new Uri(clientConfig.Url + "?access_token=" + token);
        serverConfig.AspEnableAuthentication = true;
        serverConfig.AspAuthenticationScheme = "BearerToken";
        SetAccessTokenQueryParameter(serverConfig, "access_token");

        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        if (succeeds)
        {
            await Assert.That(() => client.ConnectAsync().Timeout(TestTimeouts.Connect)).ThrowsNothing();
        }
        else
        {
            var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(TestTimeouts.Connect));
            await Assert.That(actual!.Error).IsEqualTo(TransportError.AuthenticationError);
        }
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task QueryStringTokenIsDisabledByDefault(Type type)
    {
        var clientConfig = (HttpClientConfig)CreateClientConfig(type);
        var serverConfig = (AspServerConfig)CreateServerConfig(type);

        clientConfig.Url = new Uri(clientConfig.Url + "?access_token=Token123");
        serverConfig.AspEnableAuthentication = true;
        serverConfig.AspAuthenticationScheme = "BearerToken";

        await Assert.That(GetAccessTokenQueryParameter(serverConfig)).IsNull();

        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(TestTimeouts.Connect));
        await Assert.That(actual!.Error).IsEqualTo(TransportError.AuthenticationError);
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task AuthorizationHeaderWinsOverQueryStringToken(Type type)
    {
        var clientConfig = (HttpClientConfig)CreateClientConfig(type);
        var serverConfig = (AspServerConfig)CreateServerConfig(type);

        clientConfig.Url = new Uri(clientConfig.Url + "?access_token=Token123");
        clientConfig.AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "Bad Token");
        serverConfig.AspEnableAuthentication = true;
        serverConfig.AspAuthenticationScheme = "BearerToken";
        SetAccessTokenQueryParameter(serverConfig, "access_token");

        var (client, _) = CreateClient(clientConfig);
        var server = CreateServer(
            serverConfig,
            null,
            AspCreateAuthServices,
            AspAppAuthConfigure);

        await server.StartAsync().Timeout(TestTimeouts.Connect);

        var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(TestTimeouts.Connect));
        await Assert.That(actual!.Error).IsEqualTo(TransportError.AuthenticationError);
    }

    private static void SetAccessTokenQueryParameter(AspServerConfig config, string? name)
    {
        if (config is WebSocketServerConfig ws)
            ws.AccessTokenQueryParameter = name;
        else if (config is HttpSocketServerConfig hs)
            hs.AccessTokenQueryParameter = name;
    }

    private static string? GetAccessTokenQueryParameter(AspServerConfig config) => config switch
    {
        WebSocketServerConfig ws => ws.AccessTokenQueryParameter,
        HttpSocketServerConfig hs => hs.AccessTokenQueryParameter,
        _ => null
    };
}