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
/// The policy unit tests and <see cref="ReconnectsAfterServerShutdown_WhenPolicyAllowsIt"/> use the C# policy API
/// (<see cref="DefaultReconnectionPolicy.ReconnectReasons"/>, which <see cref="ClientSpec"/> has no field for), so they
/// stay on the C# client. The other transport test runs on every row of the client matrix.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ReconnectionPolicyTests(LanguageHostPool hosts) : BaseTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are skipped.</summary>
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
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ReconnectsAfterServerShutdown_WhenPolicyAllowsIt(Type type)
    {
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) })
        {
            ReconnectReasons = new HashSet<DisconnectReason>
            {
                DisconnectReason.SocketError,
                DisconnectReason.Timeout,
                DisconnectReason.ServerRestarting,
                DisconnectReason.ServerShutdown
            }
        };

        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                reconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };

        // The server sends its ServerShutdown signal on stop (the signal is not disabled here).
        serverConfig.InternalNoLingerOnShutdown = true;

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        var disconnected = (Task<DisconnectReason>)client.DisconnectedTask;

        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await disconnected.Timeout(T(1))).IsEqualTo(DisconnectReason.ServerShutdown);

        // Restart on the same endpoint; the policy keeps retrying every 20 ms until it gets through.
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        await reconnected.Task.Timeout(T(2));
        await WaitForState(client, ConnectionState.Connected);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp])]
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
}