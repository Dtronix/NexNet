using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Transports;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Edge cases of the C# client's reconnection loop: a reconnected session lost before the loop that opened it has
/// ended, and a connect token canceled after the connect succeeded.
/// </summary>
internal partial class NexusClientTests
{
    /// <summary>
    /// A reconnected session lost before the reconnection loop that opened it has finished (held by a test hook, after
    /// the lost session's own disconnect handling ran) starts a new loop: the client reconnects again.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ReconnectedSession_LostBeforeTheLoopEnds_Reconnects(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var policy = new DefaultReconnectionPolicy([TimeSpan.Zero], continuousRetry: false);
        clientConfig.ReconnectionPolicy = policy;
        var attempts = 0;
        policy.Reconnection += (_, _) => Interlocked.Increment(ref attempts);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        var connections = 0;
        var thirdConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientNexus.OnConnectedEvent = (_, _) =>
        {
            if (Interlocked.Increment(ref connections) == 3)
                thirdConnection.TrySetResult();
            return ValueTask.CompletedTask;
        };

        var reconnections = 0;
        client.InternalOnReconnected = async () =>
        {
            // The first loop's attempt connected; its session is lost before the loop ends.
            if (Interlocked.Increment(ref reconnections) != 1)
                return;

            var lost = client.DisconnectedTask;
            await client.Session!.DisconnectAsync(DisconnectReason.SocketError).ConfigureAwait(false);
            await lost.ConfigureAwait(false);

            // The lost session's disconnect handling runs while the loop still holds the reconnection.
            await Task.Delay(200).ConfigureAwait(false);
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await client.Session!.DisconnectAsync(DisconnectReason.SocketError).Timeout(T(1));

        await thirdConnection.Task.Timeout(T(TestTimeouts.Connect));
        await WaitUntil(() => client.State == ConnectionState.Connected, T(TestTimeouts.Connect));
        await Assert.That(Volatile.Read(ref attempts)).IsEqualTo(2).Because("Each lost session started one loop of one attempt.");
    }

    /// <summary>
    /// The token passed to ConnectAsync cancels only the connect: canceled after the connect succeeded, it does not
    /// stop the client from reconnecting when the session is later lost.
    /// </summary>
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ConnectToken_CanceledAfterConnect_DoesNotDisableReconnection(Type type)
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        clientConfig.ReconnectionPolicy = new DefaultReconnectionPolicy([TimeSpan.Zero], continuousRetry: false);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clientNexus.OnConnectedEvent = (_, isReconnected) =>
        {
            if (isReconnected)
                reconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        using (var connectToken = new CancellationTokenSource())
        {
            await client.ConnectAsync(connectToken.Token).Timeout(T(TestTimeouts.Connect));
            await connectToken.CancelAsync();
        }

        await client.Session!.DisconnectAsync(DisconnectReason.SocketError).Timeout(T(1));

        await reconnected.Task.Timeout(T(TestTimeouts.Connect));
        await WaitUntil(() => client.State == ConnectionState.Connected, T(TestTimeouts.Connect));
    }
}
