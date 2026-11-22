using NexNet.Asp.WebSocket;
using NexNet.Interop;
using NexNet.InteropServer;
using NexNet.TestNexus.Interop;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// The WebSocket origin allow-list (<see cref="WebSocketServerConfig.AllowedOrigins"/>) with real clients. A browser
/// always sends the origin of its page, so only the browser rows test the allow-list itself; the other clients send no
/// Origin header and are always allowed (<c>AspWebSocketOriginTests.ClientWithoutOriginConnects</c>).
/// </summary>
internal partial class CrossLanguageSmokeTests
{
    /// <summary>An origin no client page has.</summary>
    private const string OtherOrigin = "https://allowed.example";

    /// <summary>
    /// A server whose allow-list lacks the browser's page origin refuses the browser client; the clients that send no
    /// Origin connect. Mirrors <c>AspWebSocketOriginTests.BlockedOriginIsForbidden</c> and
    /// <c>ClientWithoutOriginConnects</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke(Transports = [ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task Origin_NotInTheAllowList_RefusesOnlyABrowser(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync(setOptions: options => options.AllowedOrigins.Add(OtherOrigin));
        await using var client = await Harness.CreateClientAsync(target, server.Spec(target.Transport, "client"));
        var connected = await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect));

        if (IsBrowser(target))
        {
            await Assert.That(connected).IsFalse();
            await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
            await Assert.That(server.Server.RequestOrigins).IsNotEmpty().Because("a browser sends the origin of its page");
        }
        else
        {
            await Assert.That(connected).IsTrue();
            await Assert.That(await client.Proxy<IInteropServerNexus>().EchoInt32(1).AsTask().Timeout(T(2))).IsEqualTo(1);
            await Assert.That(server.Server.RequestOrigins).IsEmpty().Because("only a browser sends an Origin header");
        }

        await EndAsync(client);
    }

    /// <summary>
    /// A server whose allow-list has the browser's page origin, in another case, admits the browser client. Mirrors
    /// <c>AspWebSocketOriginTests.AllowedOriginConnects</c> and <c>AllowedOriginIsCaseInsensitive</c>.
    /// </summary>
    /// <remarks>
    /// Only the browser knows its page origin (<c>http://127.0.0.1:&lt;port&gt;</c> of the launcher): a first connect,
    /// refused, shows it to the server (<c>InteropServer.RequestOrigins</c>), and the test then adds it to the live
    /// allow-list in upper case.
    /// </remarks>
    [Test]
    [CrossLanguageSmoke(Transports = [ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task Origin_InTheAllowList_Connects(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync(setOptions: options => options.AllowedOrigins.Add(OtherOrigin));

        if (IsBrowser(target))
        {
            await using (var probe = await Harness.CreateClientAsync(target, server.Spec(target.Transport, "probe")))
            {
                await Assert.That(await probe.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
                await EndAsync(probe);
            }

            var origin = server.Server.RequestOrigins.First();
            await Assert.That(origin).StartsWith("http");
            var config = (WebSocketServerConfig)server.Server.Configs[target.Transport.ToEndpointKind()];
            config.AllowedOrigins.Add(origin.ToUpperInvariant());
        }

        await using var client = await server.ConnectAsync(target, "client");
        await server.SessionAsync("client");
        await Assert.That(await client.Proxy<IInteropServerNexus>().EchoInt32(2).AsTask().Timeout(T(2))).IsEqualTo(2);
        await EndAsync(client);
    }

    /// <summary>True for a client in a browser page, which always sends its page's Origin.</summary>
    private static bool IsBrowser(ClientTarget target) => target.Language.StartsWith("browser-", StringComparison.Ordinal);
}
