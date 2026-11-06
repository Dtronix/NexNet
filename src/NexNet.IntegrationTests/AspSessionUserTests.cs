using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using NexNet.Transports;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

/// <summary>
/// The ASP.NET principal (<c>HttpContext.User</c>) reaches the session as <c>Context.User</c>.
/// </summary>
internal class AspSessionUserTests : BaseAspTests
{
    private const string Scheme = "ClaimsToken";
    private const string UserName = "alice";

    /// <summary>
    /// Adds a scheme that accepts "Bearer Token123" with a principal named <see cref="UserName"/>.
    /// </summary>
    private static Action<WebApplicationBuilder> ClaimsServices(bool defaultScheme) => builder =>
    {
        var authentication = defaultScheme
            ? builder.Services.AddAuthentication(Scheme)
            : builder.Services.AddAuthentication();
        authentication.AddBearerToken(Scheme, o =>
        {
            o.Events.OnMessageReceived = context =>
            {
                if (context.Request.Headers.Authorization.FirstOrDefault() == "Bearer Token123")
                {
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, UserName), new Claim(ClaimTypes.Role, "admin")], Scheme));
                    context.Success();
                }
                else
                    context.Fail("Failed to authenticate token");

                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization();
    };

    private (ServerConfig Server, ClientConfig Client) CreateConfigs(Type type, bool aspAuthentication)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;

        if (clientConfig is HttpClientConfig httpClientConfig)
            httpClientConfig.AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "Token123");

        if (serverConfig is AspServerConfig aspServerConfig)
        {
            aspServerConfig.AspEnableAuthentication = aspAuthentication;
            aspServerConfig.AspAuthenticationScheme = aspAuthentication ? Scheme : null;
        }

        return (serverConfig, clientConfig);
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnAuthenticate_SeesAspPrincipalClaims(Type type)
    {
        var (serverConfig, clientConfig) = CreateConfigs(type, aspAuthentication: true);

        var seen = new TaskCompletionSource<ClaimsPrincipal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnAuthenticateEvent = n =>
        {
            seen.TrySetResult(n.Context.User);
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        }, ClaimsServices(defaultScheme: false), AspAppAuthConfigure);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        var user = await seen.Task.Timeout(1);
        Assert.That(user, Is.Not.Null);
        Assert.That(user!.Identity?.IsAuthenticated, Is.True);
        Assert.That(user.FindFirst(ClaimTypes.Name)?.Value, Is.EqualTo(UserName));
        Assert.That(user.IsInRole("admin"), Is.True);
    }

    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OnConnectedAndInvocation_SeeAspPrincipal(Type type)
    {
        var (serverConfig, clientConfig) = CreateConfigs(type, aspAuthentication: true);

        var connected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus =>
        {
            nexus.OnAuthenticateEvent = _ => ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            nexus.OnConnectedEvent = n =>
            {
                connected.TrySetResult(n.Context.User?.Identity?.Name);
                return ValueTask.CompletedTask;
            };
            nexus.ServerTaskValueEvent = n =>
            {
                invoked.TrySetResult(n.Context.User?.Identity?.Name);
                return ValueTask.FromResult(1);
            };
        }, ClaimsServices(defaultScheme: false), AspAppAuthConfigure);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);
        await client.Proxy.ServerTaskValue().Timeout(1);

        Assert.That(await connected.Task.Timeout(1), Is.EqualTo(UserName));
        Assert.That(await invoked.Task.Timeout(1), Is.EqualTo(UserName));
    }

    /// <summary>
    /// With <c>AspEnableAuthentication</c> off the session gets whatever the pipeline set: here
    /// <c>UseAuthentication</c> with a default scheme.
    /// </summary>
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AspAuthenticationDisabled_SessionSeesPipelinePrincipal(Type type)
    {
        var (serverConfig, clientConfig) = CreateConfigs(type, aspAuthentication: false);

        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnAuthenticateEvent = n =>
        {
            seen.TrySetResult(n.Context.User?.FindFirst(ClaimTypes.Name)?.Value);
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        }, ClaimsServices(defaultScheme: true), AspAppAuthConfigure);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        Assert.That(await seen.Task.Timeout(1), Is.EqualTo(UserName));
    }

    /// <summary>
    /// The principal is extra input: OnAuthenticate still decides, and refusing it refuses the connection.
    /// </summary>
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task NexusAuthenticationStillDecides_WithAspPrincipal(Type type)
    {
        var (serverConfig, clientConfig) = CreateConfigs(type, aspAuthentication: true);

        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnAuthenticateEvent = n =>
        {
            seen.TrySetResult(n.Context.User?.Identity?.Name);
            return ValueTask.FromResult<IIdentity?>(null);
        }, ClaimsServices(defaultScheme: false), AspAppAuthConfigure);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);
        var result = await client.TryConnectAsync().Timeout(2);

        Assert.That(await seen.Task.Timeout(1), Is.EqualTo(UserName));
        Assert.That(result.Success, Is.False);
    }

    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Uds)]
    public async Task NonAspTransport_SessionUserIsNull(Type type)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        var clientConfig = CreateClientConfig(type);

        var authenticated = new TaskCompletionSource<ClaimsPrincipal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<ClaimsPrincipal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus =>
        {
            nexus.OnAuthenticateEvent = n =>
            {
                authenticated.TrySetResult(n.Context.User);
                return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            };
            nexus.OnConnectedEvent = n =>
            {
                connected.TrySetResult(n.Context.User);
                return ValueTask.CompletedTask;
            };
        });
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(2);

        Assert.That(await authenticated.Task.Timeout(1), Is.Null);
        Assert.That(await connected.Task.Timeout(1), Is.Null);
    }

    [Test]
    public void TransportUser_DefaultsToNull()
    {
        ITransport transport = new MinimalTransport();
        Assert.That(transport.User, Is.Null);
    }

    /// <summary>
    /// A transport written before <see cref="ITransport.User"/> existed: it does not implement it.
    /// </summary>
    private sealed class MinimalTransport : ITransport
    {
        private readonly Pipe _pipe = new();
        public PipeReader Input => _pipe.Reader;
        public PipeWriter Output => _pipe.Writer;
        public string? RemoteAddress => null;
        public int? RemotePort => null;
        public X509Certificate2? RemoteCertificate => null;
        public ValueTask CloseAsync(bool linger) => ValueTask.CompletedTask;
    }
}
