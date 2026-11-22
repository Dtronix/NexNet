using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.TestNexus.Interop;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The reconnection policy decides which disconnect reasons the client reconnects for.
/// </summary>
/// <remarks>
/// The policy unit tests use the C# policy API, so they stay on the C# client. The transport tests run on every row of
/// the client matrix; <see cref="ClientSpec.ReconnectReasons"/> sets the reasons of the client under test.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ReconnectionPolicyTests(LanguageHostPool hosts) : BaseTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are generated.</summary>
    public LanguageHostPool Hosts { get; } = hosts;

    private static readonly DisconnectReason[] AllReasons = Enum.GetValues<DisconnectReason>();

    private static readonly DisconnectReason[] DefaultReasons =
    {
        DisconnectReason.SocketError,
        DisconnectReason.Timeout,
        DisconnectReason.ServerRestarting
    };

    /// <summary>
    /// A policy that implements only the members that existed before ShouldReconnect, as a user's policy would.
    /// </summary>
    private class LegacyPolicy : IReconnectionPolicy
    {
        public TimeSpan? ReconnectDelay(int retryAttempt) => TimeSpan.Zero;

        public event ReconnectionAttemptDelegate? Reconnection;

        void IReconnectionPolicy.FireReconnection(INexusClient client, int retryAttempt)
        {
            Reconnection?.Invoke(client, retryAttempt);
        }
    }

    [Test]
    public async Task InterfaceDefault_ShouldReconnect_MatchesTodaysList()
    {
        IReconnectionPolicy policy = new LegacyPolicy();

        foreach (var reason in AllReasons)
        {
            await Assert.That(policy.ShouldReconnect(reason)).IsEqualTo(DefaultReasons.Contains(reason)).Because($"Default ShouldReconnect for {reason}");
        }
    }

    [Test]
    public async Task DefaultPolicy_ReconnectReasons_DefaultsToTodaysList()
    {
        var policy = new DefaultReconnectionPolicy();

        await Assert.That(policy.ReconnectReasons).IsEquivalentTo(DefaultReasons);

        IReconnectionPolicy asInterface = policy;
        foreach (var reason in AllReasons)
        {
            await Assert.That(asInterface.ShouldReconnect(reason)).IsEqualTo(DefaultReasons.Contains(reason)).Because($"DefaultReconnectionPolicy.ShouldReconnect for {reason}");
        }
    }

    [Test]
    public async Task DefaultPolicy_ShouldReconnect_UsesReconnectReasons()
    {
        IReconnectionPolicy policy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) })
        {
            ReconnectReasons = new HashSet<DisconnectReason>
            {
                DisconnectReason.ServerShutdown,
                DisconnectReason.SocketError
            }
        };

        foreach (var reason in AllReasons)
        {
            var expected = reason is DisconnectReason.ServerShutdown or DisconnectReason.SocketError;
            await Assert.That(policy.ShouldReconnect(reason)).IsEqualTo(expected).Because($"ShouldReconnect for {reason}");
        }
    }

    [Test]
    public async Task DefaultPolicy_ReconnectReasons_NullThrows()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new DefaultReconnectionPolicy
        {
            ReconnectReasons = null!
        });
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ReconnectsAfterServerShutdown_WhenPolicyAllowsIt(ClientTarget target)
    {
        // The server sends its ServerShutdown signal on stop (the signal is not disabled here).
        await using var server = await InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(
            () => new InteropServerNexus(),
            new InteropServerOptions { ConfigureEndpoint = (_, config) => config.InternalNoLingerOnShutdown = true });

        // The default reasons plus ServerShutdown, with a 20 ms delay retried continuously.
        var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
        spec.ReconnectDelaysMs = [20];
        spec.ReconnectContinuously = true;
        spec.ReconnectReasons =
        [
            DisconnectReason.SocketError,
            DisconnectReason.Timeout,
            DisconnectReason.ServerRestarting,
            DisconnectReason.ServerShutdown,
        ];
        await using var client = await Harness.CreateClientAsync(target, spec);
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var connected = client.Callbacks<InteropClientNexusScript>().OnConnected;
        await Assert.That((await connected.NextCallAsync().AsTask().Timeout(T(1))).IsReconnected).IsFalse();

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await disconnected).IsEqualTo(DisconnectReason.ServerShutdown);

        // Restart on the same endpoint; the policy keeps retrying every 20 ms until it gets through. A connect
        // attempt to the stopped endpoint can wait for a TCP retransmit (about 500 ms on Windows).
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That((await connected.NextCallAsync().AsTask().Timeout(T(TestTimeouts.Connect))).IsReconnected).IsTrue();
        await client.WaitForStateAsync(ConnectionState.Connected, T(1));
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task DoesNotReconnectAfterServerShutdown_WithDefaultPolicy(ClientTarget target)
    {
        // The server sends its ServerShutdown signal on stop (the signal is not disabled here).
        await using var server = await InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(
            () => new InteropServerNexus(),
            new InteropServerOptions { ConfigureEndpoint = (_, config) => config.InternalNoLingerOnShutdown = true });

        // The default reasons with a 20 ms delay, retried continuously.
        var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
        spec.ReconnectDelaysMs = [20];
        spec.ReconnectContinuously = true;
        await using var client = await Harness.CreateClientAsync(target, spec);
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var connected = client.Callbacks<InteropClientNexusScript>().OnConnected;
        await Assert.That((await connected.NextCallAsync().AsTask().Timeout(T(1))).IsReconnected).IsFalse();

        var disconnected = client.WhenDisconnectedAsync(T(TestTimeouts.Connect));
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await disconnected).IsEqualTo(DisconnectReason.ServerShutdown);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        // Several policy delays pass; no reconnect may start.
        var reconnected = false;
        using (var window = new CancellationTokenSource(300))
        {
            try
            {
                await connected.NextCallAsync(window.Token);
                reconnected = true;
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested)
            {
                // No reconnection within the window.
            }
        }

        await Assert.That(reconnected).IsFalse().Because("The default policy must not reconnect after ServerShutdown.");
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected)
            .Because("The default policy must not start reconnecting after ServerShutdown.");
    }

    /// <summary>
    /// With no reconnection settings (<see cref="ClientSpec.DefaultReconnection"/>), only a browser client reconnects: the
    /// JS runtime's default policy is on in a browser and off in Node (<c>reconnect/environment.js</c>), and the C# and
    /// Java clients have none. The session times out, a reason the default policy reconnects for, and the policy's first
    /// delay is 0, so a browser client reconnects at once.
    /// </summary>
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task DefaultReconnection_OnlyABrowserClientReconnects(ClientTarget target)
    {
        await using var server = await InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus());

        // The session times out before the first ping.
        var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
        spec.DefaultReconnection = true;
        spec.PingIntervalMs = 75;
        spec.TimeoutMs = 50;
        await using var client = await Harness.CreateClientAsync(target, spec);
        var connected = client.Callbacks<InteropClientNexusScript>().OnConnected;
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That((await connected.NextCallAsync().AsTask().Timeout(T(1))).IsReconnected).IsFalse();

        if (target.Language.StartsWith("browser-", StringComparison.Ordinal))
        {
            await connected.NextCallAsync().AsTask().Timeout(T(2));
            return;
        }

        await Assert.That(await client.WhenDisconnectedAsync(T(1))).IsEqualTo(DisconnectReason.Timeout);

        // The default policy would reconnect at once; nothing may start.
        var reconnected = false;
        using (var window = new CancellationTokenSource(300))
        {
            try
            {
                await connected.NextCallAsync(window.Token);
                reconnected = true;
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested)
            {
                // No reconnection within the window.
            }
        }

        await Assert.That(reconnected).IsFalse().Because("Only a browser client reconnects by default.");
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
        var report = await client.EndTestAuditAsync().Timeout(T(5));
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }
}
