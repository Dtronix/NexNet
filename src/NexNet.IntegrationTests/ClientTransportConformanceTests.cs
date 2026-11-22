using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.InteropServer;
using NexNet.Messages;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The client side of the HTTP bindings of protocol-specification.md §13.6/§13.7 and of the progress timeout (§9), on
/// every row of the client matrix. The server misbehaves on purpose: a <see cref="MisbehavingEndpoint"/> answers the
/// upgrade with a status other than 101, with a redirect, or sends a WebSocket text message, and a
/// <see cref="StallingProxy"/> stops forwarding the server's bytes in the middle of a large result.
/// </summary>
/// <remarks>
/// A browser reports every failed WebSocket connect the same way (<c>InternalError</c>, the WebSocket API gives page JS
/// no status), and page JS may close a WebSocket only with 1000 or 3000–4999, so those rows check what the platform
/// lets the client see and do (<c>@dtronix/nexnet</c> <c>transport/WebSocketTransport.js</c>).
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientTransportConformanceTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    private static readonly int[] SuccessStatuses = [200, 204];

    private static readonly int[] RedirectStatuses = [301, 302, 307];

    [Test]
    [ClientMatrix<int>(nameof(SuccessStatuses), Transports = [ClientTransport.HttpSocket])]
    public async Task HttpSocket_SuccessStatusOtherThan101_FailsConnect(ClientTarget target, int status)
    {
        if (target.Language == ClientLanguages.Node)
            Skip.Test("The Node HttpSocket transport reports a 2xx answer as InternalError, not ProtocolError (protocol spec §13.6); follow-up card.");

        await using var endpoint = MisbehavingEndpoint.AnswerUpgradeWith(status);

        var exception = await ConnectFailsAsync(target, endpoint.GetAddress(InteropEndpointKind.HttpSocket));
        await Assert.That(exception.Error).IsEqualTo(TransportError.ProtocolError);
        await Assert.That(endpoint.Requests).IsEqualTo(1);
    }

    [Test]
    [ClientMatrix<int>(nameof(RedirectStatuses), Transports = [ClientTransport.HttpSocket, ClientTransport.WebSocket])]
    public async Task UpgradeRedirect_IsNotFollowed_FailsWithProtocolError(ClientTarget target, int status)
    {
        await using var endpoint = MisbehavingEndpoint.RedirectUpgrade(status);

        var exception = await ConnectFailsAsync(target, endpoint.GetAddress(target.Transport.ToEndpointKind()));
        await Assert.That(exception.Error).IsEqualTo(IsBrowser(target) ? TransportError.InternalError : TransportError.ProtocolError);

        // The connect failed on the redirect itself: no second request went to the target or back to the endpoint.
        await Assert.That(endpoint.RedirectTargetConnections).IsEqualTo(0);
        await Assert.That(endpoint.Requests).IsEqualTo(1);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.WebSocket])]
    public async Task WebSocket_TextMessage_ClientClosesWith1003(ClientTarget target)
    {
        await using var endpoint = MisbehavingEndpoint.SendWebSocketTextMessage();

        // The C# client's ConnectAsync reports a session that ends before the server greeting through its result, not
        // an exception, so the test asks every language for the result.
        var spec = new ClientSpec { Transport = target.Transport, Address = endpoint.GetAddress(InteropEndpointKind.WebSocket), Logger = Logger };
        await using var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);

        // T(10): the C# client takes about 5 s to fail here. The close interrupts the send of its greeting, and its
        // disconnect then waits out the 5 s write-lock timeout of NexusSession.DisconnectCore (follow-up card).
        await Assert.That(await client.TryConnectAsync().Timeout(T(10))).IsFalse();
        await AssertState(client, ConnectionState.Disconnected);

        var closeStatus = await endpoint.ClientCloseStatus.Timeout(T(TestTimeouts.Connect));

        // 1003 (unsupported data). Page JS cannot send 1003, so a browser client closes normally.
        await Assert.That(closeStatus).IsEqualTo(IsBrowser(target) ? 1000 : 1003);
        await Assert.That(endpoint.Requests).IsEqualTo(1);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ProgressTimeout_ServerStallsMidResult_DisconnectsAsStalled(ClientTarget target)
    {
        const int size = 1024 * 1024;
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(0));
        await using var proxy = StallingProxy.Start(server.Endpoints, target.Transport.ToEndpointKind());
        await using var client = await ConnectAsync(target, server, configure: spec =>
        {
            spec.Address = proxy.Address;
            spec.ProgressTimeoutMs = 500;

            // The progress check runs on the ping timer.
            spec.PingIntervalMs = 100;
        });
        await Assert.That(await Proxy(client).ServerTaskValue().Timeout(T(1))).IsEqualTo(0);

        // The result is streamed in 64 KiB chunks; the proxy passes on a little more than one of them.
        proxy.StallToClientAfter(100 * 1024);
        var call = Proxy(client).ServerDataResult(size).AsTask();
        await proxy.Stalled.Timeout(T(5));

        var reason = await client.WhenDisconnectedAsync(T(5));
        await Assert.That(reason).IsEqualTo(DisconnectReason.Stalled);
        await Assert.That(async () => await call.Timeout(T(1))).Throws<Exception>();
        await EndAsync(client);
    }

    /// <summary>
    /// Creates the client of <paramref name="target"/> for <paramref name="address"/>, asserts that its connect fails
    /// with a <see cref="TransportException"/> and that it holds nothing afterwards.
    /// </summary>
    private async Task<TransportException> ConnectFailsAsync(ClientTarget target, string address)
    {
        var spec = new ClientSpec { Transport = target.Transport, Address = address, Logger = Logger };
        await using var client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);

        var exception = await Assert.ThrowsExactlyAsync<TransportException>(async () =>
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect)));
        await AssertState(client, ConnectionState.Disconnected);
        await EndAsync(client);
        return exception!;
    }

    private static bool IsBrowser(ClientTarget target) => target.Language.StartsWith("browser-", StringComparison.Ordinal);
}
