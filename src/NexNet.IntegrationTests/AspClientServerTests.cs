using System.Net.Http.Headers;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal partial class AspClientServerTests : BaseAspTests
{
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
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

        await server.StartAsync().Timeout(1);
        Assert.DoesNotThrowAsync(() => client.ConnectAsync().Timeout(1));
        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
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
        
        await server.StartAsync().Timeout(1);
        
        var actual = Assert.ThrowsAsync<TransportException>(() => client.ConnectAsync().Timeout(1));
        Assert.That(actual.Error, Is.EqualTo(TransportError.AuthenticationError));
    }
    
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
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
        
        await server.StartAsync().Timeout(1);
        
        var actual = Assert.ThrowsAsync<TransportException>(() => client.ConnectAsync().Timeout(1));
        Assert.That(actual.Error, Is.EqualTo(TransportError.AuthenticationError));
    }
    
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
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
        
        await server.StartAsync().Timeout(1);
  
        await client.ConnectAsync().Timeout(1);
    }
    
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
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
        
        await server.StartAsync().Timeout(1);
        
        var actual = Assert.ThrowsAsync<TransportException>(() => client.ConnectAsync().Timeout(1));
        Assert.That(actual.Error, Is.EqualTo(TransportError.InternalError));
    }
    
}
