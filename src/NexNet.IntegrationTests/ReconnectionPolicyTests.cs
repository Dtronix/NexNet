using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The reconnection policy decides which disconnect reasons the client reconnects for.
/// </summary>
internal class ReconnectionPolicyTests : BaseTests
{
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
    public void InterfaceDefault_ShouldReconnect_MatchesTodaysList()
    {
        IReconnectionPolicy policy = new LegacyPolicy();

        foreach (var reason in AllReasons)
        {
            Assert.That(policy.ShouldReconnect(reason), Is.EqualTo(DefaultReasons.Contains(reason)),
                $"Default ShouldReconnect for {reason}");
        }
    }

    [Test]
    public void DefaultPolicy_ReconnectReasons_DefaultsToTodaysList()
    {
        var policy = new DefaultReconnectionPolicy();

        Assert.That(policy.ReconnectReasons, Is.EquivalentTo(DefaultReasons));

        IReconnectionPolicy asInterface = policy;
        foreach (var reason in AllReasons)
        {
            Assert.That(asInterface.ShouldReconnect(reason), Is.EqualTo(DefaultReasons.Contains(reason)),
                $"DefaultReconnectionPolicy.ShouldReconnect for {reason}");
        }
    }

    [Test]
    public void DefaultPolicy_ShouldReconnect_UsesReconnectReasons()
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
            Assert.That(policy.ShouldReconnect(reason), Is.EqualTo(expected), $"ShouldReconnect for {reason}");
        }
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
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

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var disconnected = (Task<DisconnectReason>)client.DisconnectedTask;

        await server.StopAsync().Timeout(1);
        Assert.That(await disconnected.Timeout(1), Is.EqualTo(DisconnectReason.ServerShutdown));

        // Restart on the same endpoint; the policy keeps retrying every 20 ms until it gets through.
        await server.StartAsync().Timeout(1);

        await reconnected.Task.Timeout(2);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task DoesNotReconnectAfterServerShutdown_WithDefaultPolicy(Type type)
    {
        var reconnecting = false;
        var reconnected = false;
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy(new[] { TimeSpan.FromMilliseconds(20) });

        clientNexus.OnReconnectingEvent = _ =>
        {
            reconnecting = true;
            return ValueTask.CompletedTask;
        };
        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                reconnected = true;
            return ValueTask.CompletedTask;
        };

        serverConfig.InternalNoLingerOnShutdown = true;

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var disconnected = (Task<DisconnectReason>)client.DisconnectedTask;

        await server.StopAsync().Timeout(1);
        Assert.That(await disconnected.Timeout(1), Is.EqualTo(DisconnectReason.ServerShutdown));

        await server.StartAsync().Timeout(1);

        // Several policy delays pass; no reconnect may start.
        await Task.Delay(300);
        Assert.That(reconnecting, Is.False, "The default policy must not reconnect after ServerShutdown.");
        Assert.That(reconnected, Is.False, "The default policy must not reconnect after ServerShutdown.");
        Assert.That(client.State, Is.EqualTo(ConnectionState.Disconnected));
    }
}
