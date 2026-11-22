using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.TestNexus.Interop;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.IntegrationTests;

/// <summary>
/// The ASP.NET principal (<c>HttpContext.User</c>) reaches the session as <c>Context.User</c>.
/// </summary>
/// <remarks>
/// The client-facing tests (the client's <c>Authorization</c> header reaches the ASP.NET authentication; the server's
/// refusal reaches the client; a non-ASP transport carries no principal) run on every row of the client matrix against
/// the interop nexus (<see cref="InteropServerNexus"/>) hosted in this suite's ASP.NET app. The tests of the server's
/// pipeline choices (which scheme fills the principal, what <c>OnConnected</c> and invocations see) have the same
/// client part and stay on the C# client.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class AspSessionUserTests(LanguageHostPool hosts) : BaseAspTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are generated.</summary>
    public LanguageHostPool Hosts { get; } = hosts;

    private const string Scheme = "ClaimsToken";
    private const string UserName = "alice";
    private const string OtherScheme = "OtherDefault";
    private const string OtherUserName = "bob";

    /// <summary>
    /// Adds a scheme that accepts "Bearer Token123" with a principal named <see cref="UserName"/>.
    /// </summary>
    private static Action<WebApplicationBuilder> ClaimsServices(bool defaultScheme) => builder =>
    {
        var authentication = defaultScheme
            ? builder.Services.AddAuthentication(Scheme)
            : builder.Services.AddAuthentication();
        AddClaimsScheme(authentication);
        builder.Services.AddAuthorization();
    };

    /// <summary>
    /// Adds <see cref="Scheme"/> and a default scheme that always succeeds with a principal named
    /// <see cref="OtherUserName"/>, so <c>UseAuthentication</c> fills <c>HttpContext.User</c> with the other principal.
    /// </summary>
    private static Action<WebApplicationBuilder> OtherDefaultSchemeServices() => builder =>
    {
        var authentication = builder.Services.AddAuthentication(OtherScheme);
        AddClaimsScheme(authentication);
        authentication.AddBearerToken(OtherScheme, o =>
        {
            o.Events.OnMessageReceived = context =>
            {
                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, OtherUserName)], OtherScheme));
                context.Success();
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization();
    };

    private static void AddClaimsScheme(AuthenticationBuilder authentication)
    {
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
    }

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

    /// <summary>The suite transport of a client-matrix transport.</summary>
    private static Type ToType(ClientTransport transport) => transport switch
    {
        ClientTransport.Uds => Type.Uds,
        ClientTransport.Tcp => Type.Tcp,
        ClientTransport.TcpTls => Type.TcpTls,
        ClientTransport.HttpSocket => Type.HttpSocket,
        ClientTransport.HttpSocketTls => Type.HttpSocketTls,
        ClientTransport.WebSocket => Type.WebSocket,
        ClientTransport.WebSocketTls => Type.WebSocketTls,
        _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
    };

    /// <summary>
    /// The client spec of the server this test created for <paramref name="type"/> (same address as
    /// <see cref="BaseTests.CreateClientConfig"/>), sending the bearer token the claims scheme accepts.
    /// </summary>
    private ClientSpec CreateClientSpec(Type type)
    {
        return CreateClientConfig(type) switch
        {
            UdsClientConfig uds => new ClientSpec { Address = "uds:" + uds.EndPoint },
            // The suite's TLS server certificate is not the interop test CA's.
            TcpTlsClientConfig tls => new ClientSpec { Address = "tls://" + tls.EndPoint, InsecureSkipVerify = true },
            TcpClientConfig tcp => new ClientSpec { Address = "tcp://" + tcp.EndPoint },
            HttpClientConfig http => new ClientSpec { Address = http.Url.ToString(), AuthorizationHeader = "Bearer Token123" },
            var other => throw new NotSupportedException(other.GetType().Name),
        };
    }

    /// <summary>A server configuration that requires authentication, with ASP.NET authentication on or off.</summary>
    private ServerConfig CreateAuthServerConfig(Type type, bool aspAuthentication)
    {
        var serverConfig = CreateServerConfig(type);
        serverConfig.Authenticate = true;
        if (serverConfig is AspServerConfig aspServerConfig)
        {
            aspServerConfig.AspEnableAuthentication = aspAuthentication;
            aspServerConfig.AspAuthenticationScheme = aspAuthentication ? Scheme : null;
        }

        return serverConfig;
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.AuthorizationHeader)]
    public async Task OnAuthenticate_SeesAspPrincipalClaims(ClientTarget target)
    {
        target.SkipIfUnsupported();
        var type = ToType(target.Transport);
        var serverConfig = CreateAuthServerConfig(type, aspAuthentication: true);

        var seen = new TaskCompletionSource<ClaimsPrincipal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer<InteropServerNexus, InteropServerNexus.ClientProxy>(serverConfig, nexus => nexus.TokenValidator = (_, _) =>
        {
            seen.TrySetResult(nexus.Context.User);
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        }, ClaimsServices(defaultScheme: false), AspAppAuthConfigure);
        await using var client = await Harness.CreateClientAsync(target, CreateClientSpec(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(2));

        var user = await seen.Task.Timeout(T(1));
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Identity?.IsAuthenticated).IsTrue();
        await Assert.That(user.FindFirst(ClaimTypes.Name)?.Value).IsEqualTo(UserName);
        await Assert.That(user.IsInRole("admin")).IsTrue();
    }

    /// <summary>
    /// When the default scheme run by <c>UseAuthentication</c> is not <c>AspAuthenticationScheme</c>, the session
    /// still sees the <c>AspAuthenticationScheme</c> principal.
    /// </summary>
    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
    public async Task OtherDefaultScheme_SessionSeesAspAuthenticationSchemePrincipal(Type type)
    {
        var (serverConfig, clientConfig) = CreateConfigs(type, aspAuthentication: true);

        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.OnAuthenticateEvent = n =>
        {
            seen.TrySetResult(n.Context.User?.FindFirst(ClaimTypes.Name)?.Value);
            return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
        }, OtherDefaultSchemeServices(), AspAppAuthConfigure);
        var (client, _) = CreateClient(clientConfig);

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await client.ConnectAsync().Timeout(2);

        await Assert.That(await seen.Task.Timeout(1)).IsEqualTo(UserName);
    }

    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
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

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await client.ConnectAsync().Timeout(2);
        await client.Proxy.ServerTaskValue().Timeout(1);

        await Assert.That(await connected.Task.Timeout(1)).IsEqualTo(UserName);
        await Assert.That(await invoked.Task.Timeout(1)).IsEqualTo(UserName);
    }

    /// <summary>
    /// With <c>AspEnableAuthentication</c> off the session gets whatever the pipeline set: here
    /// <c>UseAuthentication</c> with a default scheme.
    /// </summary>
    [Test]
    [Transports(Type.WebSocket, Type.HttpSocket)]
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

        await server.StartAsync().Timeout(TestTimeouts.Connect);
        await client.ConnectAsync().Timeout(2);

        await Assert.That(await seen.Task.Timeout(1)).IsEqualTo(UserName);
    }

    /// <summary>
    /// The principal is extra input: OnAuthenticate still decides, and refusing it refuses the connection.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.AuthorizationHeader)]
    public async Task NexusAuthenticationStillDecides_WithAspPrincipal(ClientTarget target)
    {
        target.SkipIfUnsupported();
        var type = ToType(target.Transport);
        var serverConfig = CreateAuthServerConfig(type, aspAuthentication: true);

        var seen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer<InteropServerNexus, InteropServerNexus.ClientProxy>(serverConfig, nexus => nexus.TokenValidator = (_, _) =>
        {
            seen.TrySetResult(nexus.Context.User?.Identity?.Name);
            return ValueTask.FromResult<IIdentity?>(null);
        }, ClaimsServices(defaultScheme: false), AspAppAuthConfigure);
        await using var client = await Harness.CreateClientAsync(target, CreateClientSpec(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        var connected = await client.TryConnectAsync().Timeout(T(2));

        await Assert.That(await seen.Task.Timeout(T(1))).IsEqualTo(UserName);
        await Assert.That(connected).IsFalse();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls])]
    public async Task NonAspTransport_SessionUserIsNull(ClientTarget target)
    {
        target.SkipIfUnsupported();
        var type = ToType(target.Transport);
        var serverConfig = CreateAuthServerConfig(type, aspAuthentication: false);

        var authenticated = new TaskCompletionSource<ClaimsPrincipal?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionNexus = new TaskCompletionSource<InteropServerNexus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer<InteropServerNexus, InteropServerNexus.ClientProxy>(serverConfig, nexus =>
        {
            sessionNexus.TrySetResult(nexus);
            nexus.TokenValidator = (_, _) =>
            {
                authenticated.TrySetResult(nexus.Context.User);
                return ValueTask.FromResult<IIdentity?>(new DefaultIdentity());
            };
        });
        await using var client = await Harness.CreateClientAsync(target, CreateClientSpec(type));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        // TcpTls row: a TLS handshake in the loaded parallel suite can take more than 2 s (see TestTimeouts.Connect).
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        // During authentication and once connected.
        await Assert.That(await authenticated.Task.Timeout(T(1))).IsNull();
        await Assert.That((await sessionNexus.Task.Timeout(T(1))).Context.User).IsNull();
    }

    [Test]
    public async Task TransportUser_DefaultsToNull()
    {
        ITransport transport = new MinimalTransport();
        await Assert.That(transport.User).IsNull();
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