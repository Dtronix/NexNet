using NexNet.Asp.HttpSocket;
using NexNet.Asp.WebSocket;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Transports;

namespace NexNet.IntegrationTests;

/// <summary>
/// ASP.NET authentication of the HttpSocket and WebSocket endpoints (<c>AspEnableAuthentication</c>): the HTTP
/// <c>Authorization</c> header and the opt-in query-string access token (F06), on the client matrix (P01d).
/// </summary>
/// <remarks>
/// The header tests need <see cref="ClientCapabilities.AuthorizationHeader"/>, which browsers lack; the query-string
/// token tests run on every row, browsers included. A browser cannot see the HTTP status of a refused WebSocket upgrade,
/// so on browser rows a refused authentication is <see cref="TransportError.InternalError"/>
/// (<see cref="AuthRefusedError"/>; a documented browser parity limit, J34).
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal partial class AspClientServerTests(LanguageHostPool hosts) : BaseAspTests
{
    /// <summary>The language hosts of the session; initializing them registers their drivers with <see cref="Harness"/>.</summary>
    protected LanguageHostPool Hosts { get; } = hosts;

    /// <summary>
    /// Starts a server with the bearer-token authentication of <see cref="BaseAspTests"/> for <paramref name="target"/>'s
    /// transport and creates (not connects) the client under test. <paramref name="configureServer"/> sets up the server
    /// configuration; <paramref name="configureClient"/> the client spec.
    /// </summary>
    private async Task<(AspServerConfig ServerConfig, IClientUnderTest Client)> CreateAsync(
        ClientTarget target,
        Action<AspServerConfig> configureServer,
        Action<ClientSpec>? configureClient = null)
    {
        var type = MatrixTestBase.ToType(target.Transport);
        var serverConfig = (AspServerConfig)CreateServerConfig(type);
        configureServer(serverConfig);
        var server = CreateServer(serverConfig, null, AspCreateAuthServices, AspAppAuthConfigure);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var spec = MatrixTestBase.Spec(CreateClientConfig(type), target.Transport, Logger);
        configureClient?.Invoke(spec);
        var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);
        return (serverConfig, client);
    }

    private static void EnableBearerAuthentication(AspServerConfig config, string scheme = "BearerToken")
    {
        config.AspEnableAuthentication = true;
        config.AspAuthenticationScheme = scheme;
    }

    /// <summary>
    /// The error a client of <paramref name="target"/> reports when ASP.NET authentication refuses its upgrade.
    /// <see cref="TransportError.AuthenticationError"/>, except in a browser: the platform <c>WebSocket</c> hides the
    /// HTTP status of a refused upgrade, so the JS client reports every connect failure as
    /// <see cref="TransportError.InternalError"/> (docs/internals/js-runtime-contract.md, transport errors).
    /// </summary>
    private static TransportError AuthRefusedError(ClientTarget target)
        => target.Language.StartsWith("browser-", StringComparison.Ordinal)
            ? TransportError.InternalError
            : TransportError.AuthenticationError;

    private static async Task AssertConnectFails(IClientUnderTest client, TransportError error)
    {
        var actual = await Assert.ThrowsExactlyAsync<TransportException>(() => client.ConnectAsync().Timeout(T(TestTimeouts.Connect)));
        await Assert.That(actual!.Error).IsEqualTo(error);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.AuthorizationHeader)]
    public async Task ClientAuthenticationSucceeds(ClientTarget target)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (serverConfig, created) = await CreateAsync(
            target,
            config => EnableBearerAuthentication(config),
            spec => spec.AuthorizationHeader = "Bearer Token123");
        await using var client = created;
        serverConfig.InternalOnConnect = () => tcs.SetResult();

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await tcs.Task.Timeout(T(1));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.AuthorizationHeader)]
    public async Task ClientAuthenticationFailsWithBadAuth(ClientTarget target)
    {
        var (_, created) = await CreateAsync(
            target,
            config => EnableBearerAuthentication(config),
            spec => spec.AuthorizationHeader = "Bearer Bad Token");
        await using var client = created;

        await AssertConnectFails(client, TransportError.AuthenticationError);
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket])]
    public async Task ClientAuthenticationFailsWithNoAuth(ClientTarget target)
    {
        var (_, created) = await CreateAsync(target, config => EnableBearerAuthentication(config));
        await using var client = created;

        await AssertConnectFails(client, AuthRefusedError(target));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.AuthorizationHeader)]
    public async Task ServerIgnoresAuthenticationWhenNotEnabled(ClientTarget target)
    {
        var (_, created) = await CreateAsync(
            target,
            config => config.AspEnableAuthentication = false,
            spec => spec.AuthorizationHeader = "Bearer Bad Token");
        await using var client = created;

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.AuthorizationHeader)]
    public async Task ClientDoesNotConnectWhenServerSelectsBadAuthScheme(ClientTarget target)
    {
        var (_, created) = await CreateAsync(
            target,
            config => EnableBearerAuthentication(config, "BadScheme"),
            spec => spec.AuthorizationHeader = "Bearer Token123");
        await using var client = created;

        await AssertConnectFails(client, TransportError.InternalError);
        await MatrixTestBase.EndAsync(client);
    }

    // One test per token: a ClientMatrix row cannot be combined with [Arguments].
    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket])]
    public Task QueryStringTokenAuthenticatesWhenEnabled(ClientTarget target)
        => QueryStringTokenWhenEnabled(target, "Token123", succeeds: true);

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket])]
    public Task QueryStringTokenRejectedWhenInvalid(ClientTarget target)
        => QueryStringTokenWhenEnabled(target, "BadToken", succeeds: false);

    private async Task QueryStringTokenWhenEnabled(ClientTarget target, string token, bool succeeds)
    {
        var (_, created) = await CreateAsync(
            target,
            config =>
            {
                EnableBearerAuthentication(config);
                SetAccessTokenQueryParameter(config, "access_token");
            },
            spec => spec.Address += "?access_token=" + token);
        await using var client = created;

        if (succeeds)
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        else
            await AssertConnectFails(client, AuthRefusedError(target));

        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket])]
    public async Task QueryStringTokenIsDisabledByDefault(ClientTarget target)
    {
        AspServerConfig? server = null;
        var (_, created) = await CreateAsync(
            target,
            config =>
            {
                EnableBearerAuthentication(config);
                server = config;
            },
            spec => spec.Address += "?access_token=Token123");
        await using var client = created;

        await Assert.That(GetAccessTokenQueryParameter(server!)).IsNull();
        await AssertConnectFails(client, AuthRefusedError(target));
        await MatrixTestBase.EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.AuthorizationHeader)]
    public async Task AuthorizationHeaderWinsOverQueryStringToken(ClientTarget target)
    {
        var (_, created) = await CreateAsync(
            target,
            config =>
            {
                EnableBearerAuthentication(config);
                SetAccessTokenQueryParameter(config, "access_token");
            },
            spec =>
            {
                spec.Address += "?access_token=Token123";
                spec.AuthorizationHeader = "Bearer Bad Token";
            });
        await using var client = created;

        await AssertConnectFails(client, TransportError.AuthenticationError);
        await MatrixTestBase.EndAsync(client);
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
