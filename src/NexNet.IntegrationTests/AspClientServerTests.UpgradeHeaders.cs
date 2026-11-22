using System.Collections.Concurrent;
using System.Net.Http.Headers;
using NexNet.Interop;
using NexNet.IntegrationTests.TestInterfaces;

namespace NexNet.IntegrationTests;

internal partial class AspClientServerTests
{
    /// <summary>
    /// A custom header of the WebSocket or HttpSocket upgrade request (<see cref="ClientSpec.UpgradeHeaders"/>) reaches
    /// the ASP.NET request (H43, S54). Every client but the browser (<see cref="ClientCapabilities.UpgradeHeaders"/>):
    /// a browser cannot send headers.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.UpgradeHeaders)]
    public async Task CustomUpgradeHeaderReachesTheServer(ClientTarget target)
    {
        const string name = "X-NexNet-Test";
        var seen = new ConcurrentQueue<string>();
        var type = MatrixTestBase.ToType(target.Transport);
        var server = CreateServer(CreateServerConfig(type), null, null, app => app.Use(async (context, next) =>
        {
            seen.Enqueue(context.Request.Headers[name].ToString());
            await next(context).ConfigureAwait(false);
        }));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var spec = MatrixTestBase.Spec(CreateClientConfig(type), target.Transport, Logger);
        spec.UpgradeHeaders = new Dictionary<string, string> { [name] = "value 1" };
        await using var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(seen).Contains("value 1");
        await MatrixTestBase.EndAsync(client);
    }

    /// <summary>
    /// A header the client refuses fails client creation on every language that sends headers (J43): a header the
    /// transport sets (<c>Host</c>, <c>Sec-WebSocket-Key</c>), a name that is not an HTTP token, and a value with CR/LF.
    /// C# <c>UpgradeHeaderCollection</c> and Java <c>ClientConfig.Builder.header</c> throw at once, JS at
    /// <c>createClient</c> (<c>TypeError</c>); every host answers <c>BadRequest</c>, which the harness raises as
    /// <see cref="ArgumentException"/>.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket], Requires = ClientCapabilities.UpgradeHeaders)]
    public async Task RefusedUpgradeHeaderFailsClientCreation(ClientTarget target)
    {
        var type = MatrixTestBase.ToType(target.Transport);
        (string Name, string Value)[] refused =
        [
            ("Host", "evil.example"),
            ("Sec-WebSocket-Key", "x"),
            ("X Space", "x"),
            ("X-Injected", "a\r\nX-Smuggled: 1"),
        ];
        foreach (var (name, value) in refused)
        {
            var spec = MatrixTestBase.Spec(CreateClientConfig(type), target.Transport, Logger);
            spec.UpgradeHeaders = new Dictionary<string, string> { [name] = value };
            await Assert.That(async () =>
                {
                    await using var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);
                })
                .ThrowsExactly<ArgumentException>()
                .Because($"the {name} header must be refused");
        }
    }

    /// <summary>
    /// The typed <see cref="ClientSpec.AuthorizationHeader"/> wins over an <c>Authorization</c> entry of
    /// <see cref="ClientSpec.UpgradeHeaders"/>, and the other entries are sent with it, on every language (H57): C#
    /// <c>AuthenticationHeader</c>, Java <c>ClientConfig.Builder.authorization</c> and the Node header options. Not the
    /// browser, which cannot send headers.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket, ClientTransport.HttpSocket],
        Requires = ClientCapabilities.UpgradeHeaders | ClientCapabilities.AuthorizationHeader)]
    public async Task AuthorizationHeaderWinsOverUpgradeHeaderAuthorization(ClientTarget target)
    {
        var seen = new ConcurrentQueue<(string Authorization, string Custom)>();
        var type = MatrixTestBase.ToType(target.Transport);
        var server = CreateServer(CreateServerConfig(type), null, null, app => app.Use(async (context, next) =>
        {
            seen.Enqueue((context.Request.Headers.Authorization.ToString(), context.Request.Headers["X-NexNet-Test"].ToString()));
            await next(context).ConfigureAwait(false);
        }));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var spec = MatrixTestBase.Spec(CreateClientConfig(type), target.Transport, Logger);
        spec.AuthorizationHeader = "Bearer Typed";
        spec.UpgradeHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer FromUpgradeHeaders",
            ["X-NexNet-Test"] = "custom",
        };
        await using var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(seen).Contains(("Bearer Typed", "custom"));
        await Assert.That(seen.Any(s => s.Authorization.Contains("FromUpgradeHeaders", StringComparison.Ordinal))).IsFalse();
        await MatrixTestBase.EndAsync(client);
    }

    /// <summary>
    /// C# <see cref="NexNet.Transports.HttpClientConfig.AuthenticationHeader"/>, when set, replaces an
    /// <c>Authorization</c> entry of <see cref="NexNet.Transports.HttpClientConfig.UpgradeHeaders"/>; the other entries
    /// are sent with it, and without it the entry is sent (S54).
    /// </summary>
    [Test]
    [Arguments(Type.WebSocket, true)]
    [Arguments(Type.WebSocket, false)]
    [Arguments(Type.HttpSocket, true)]
    [Arguments(Type.HttpSocket, false)]
    public async Task AuthenticationHeaderReplacesUpgradeHeaderAuthorization(Type type, bool typedHeader)
    {
        var seen = new ConcurrentQueue<(string Authorization, string Custom)>();
        var server = CreateServer(CreateServerConfig(type), null, null, app => app.Use(async (context, next) =>
        {
            seen.Enqueue((context.Request.Headers.Authorization.ToString(), context.Request.Headers["X-NexNet-Test"].ToString()));
            await next(context).ConfigureAwait(false);
        }));
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        var config = (NexNet.Transports.HttpClientConfig)CreateClientConfig(type);
        config.UpgradeHeaders["Authorization"] = "Bearer FromUpgradeHeaders";
        config.UpgradeHeaders["X-NexNet-Test"] = "custom";
        if (typedHeader)
            config.AuthenticationHeader = new AuthenticationHeaderValue("Bearer", "Typed");
        var (client, _) = CreateClient(config);

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var expected = typedHeader ? "Bearer Typed" : "Bearer FromUpgradeHeaders";
        await Assert.That(seen).Contains((expected, "custom"));
        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
    }
}
