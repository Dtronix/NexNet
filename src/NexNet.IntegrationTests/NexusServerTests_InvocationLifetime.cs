using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;
#pragma warning disable CS1998
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// Pins down the current invocation lifetime behavior on the server:
/// 1. There is no server (or client) side execution deadline on an invocation.
/// 2. A hung invocation holds its MaxConcurrentConnectionInvocations semaphore slot indefinitely,
///    starving subsequent invocations on the session.
/// 3. While the session is saturated, the read loop is blocked awaiting the semaphore, so
///    InvocationCancellationMessages queued behind a pending invocation are never dispatched.
/// 4. Session disconnection disposes registered CancellationTokenSources without cancelling them,
///    so running invocations survive the disconnect as orphaned tasks.
/// These tests assert the current behavior; introducing an invocation timeout or
/// cancel-on-disconnect will require updating them.
/// </summary>
internal class NexusServerTests_InvocationLifetime : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task InvocationHasNoExecutionTimeout(Type type)
    {
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Both sides share one fake clock so any TimeProvider-based deadline would fire on Advance.
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        serverConfig.Time = fakeTime;
        clientConfig.Time = fakeTime;
        serverConfig.Timeout = 300_000;
        clientConfig.Timeout = 300_000;

        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskValueEvent = async _ =>
            {
                startedTcs.TrySetResult();
                return await hangTcs.Task;
            };
            nexus.ServerTaskEvent = _ => ValueTask.CompletedTask;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var invocationTask = client.Proxy.ServerTaskValue().AsTask();
        await startedTcs.Task.Timeout(1);

        // Advance one hour in one-minute steps. A round-trip after each step keeps
        // LastReceived fresh on both sides so the inactivity watchdogs stay quiet.
        for (var i = 0; i < 60; i++)
        {
            fakeTime.Advance(TimeSpan.FromMinutes(1));
            await client.Proxy.ServerTask().AsTask().Timeout(1);
        }

        // Neither side imposes a deadline; the invocation just stays pending and the
        // connection remains healthy.
        Assert.That(invocationTask.IsCompleted, Is.False);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));

        // Once the method finally returns, the invocation completes normally.
        hangTcs.TrySetResult(42);
        Assert.That(await invocationTask.Timeout(1), Is.EqualTo(42));
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task HungInvocationStarvesSessionInvocationSlots(Type type)
    {
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInvocationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskEvent = async _ =>
            {
                startedTcs.TrySetResult();
                await hangTcs.Task;
            };
            nexus.ServerVoidEvent = _ => secondInvocationTcs.TrySetResult();
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var firstInvocationTask = client.Proxy.ServerTask().AsTask();
        await startedTcs.Task.Timeout(1);

        // The hung invocation holds the only semaphore slot, so the second invocation
        // is never dispatched.
        client.Proxy.ServerVoid();
        await secondInvocationTcs.Task.AssertTimeout(0.5);

        // Releasing the hung invocation frees the slot and the queued invocation runs.
        hangTcs.TrySetResult();
        await secondInvocationTcs.Task.Timeout(1);
        await firstInvocationTask.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task SaturatedSessionCannotProcessCancellation(Type type)
    {
        await RunCancellationScenario(type, saturateSession: true);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task CancellationIsProcessedWhenSessionIsNotSaturated(Type type)
    {
        // Control test for SaturatedSessionCannotProcessCancellation: with a free read
        // loop, the same cancellation reaches the method's token promptly.
        await RunCancellationScenario(type, saturateSession: false);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DisconnectDoesNotCancelRunningInvocation(Type type)
    {
        // Session teardown disposes the invocation's CancellationTokenSource without
        // cancelling it, so the method's token never fires and the invocation keeps
        // running as an orphaned task.
        await RunDisconnectScenario(type, withCancellationToken: true);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task DisconnectDoesNotStopInvocationWithoutCancellationToken(Type type)
    {
        // Without a CancellationToken parameter there is no cancellation mechanism at
        // all; the invocation keeps running after the session is gone.
        await RunDisconnectScenario(type, withCancellationToken: false);
    }

    private async Task RunCancellationScenario(Type type, bool saturateSession)
    {
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedInvocationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Two slots: one for the cancellable invocation, one for a separate blocker. Releasing
        // the blocker frees a slot while the cancellable invocation is still running.
        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxConcurrentConnectionInvocations = 2;
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskWithCancellationEvent = async (_, token) =>
            {
                token.Register(() => cancelledTcs.TrySetResult());
                startedTcs.TrySetResult();
                await hangTcs.Task;
            };
            nexus.ServerTaskEvent = async _ =>
            {
                blockerStartedTcs.TrySetResult();
                await blockerTcs.Task;
            };
            nexus.ServerVoidEvent = _ => queuedInvocationTcs.TrySetResult();
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var cts = new CancellationTokenSource();
        var invocationTask = client.Proxy.ServerTaskWithCancellation(cts.Token).AsTask();
        await startedTcs.Task.Timeout(1);

        Task? blockerTask = null;
        if (saturateSession)
        {
            blockerTask = client.Proxy.ServerTask().AsTask();
            await blockerStartedTcs.Task.Timeout(1);

            // Both slots are taken, so this invocation blocks the read loop in the semaphore
            // wait. The cancellation message sent below is ordered behind it on the wire.
            client.Proxy.ServerVoid();
        }

        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await invocationTask);

        if (saturateSession)
        {
            // The cancellation was sent, but the server does not process it while saturated.
            await cancelledTcs.Task.AssertTimeout(0.5);

            // Freeing a slot unblocks the read loop: the queued invocation is dispatched and
            // the cancellation behind it reaches the still-running method's token.
            blockerTcs.TrySetResult();
            await blockerTask!.Timeout(1);
            await queuedInvocationTcs.Task.Timeout(1);
        }

        await cancelledTcs.Task.Timeout(1);

        // Cleanup: release the hung invocation.
        hangTcs.TrySetResult();
    }

    private async Task RunDisconnectScenario(Type type, bool withCancellationToken)
    {
        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var methodExitedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask Hang()
        {
            startedTcs.TrySetResult();
            try
            {
                await hangTcs.Task;
            }
            finally
            {
                methodExitedTcs.TrySetResult();
            }
        }

        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskWithCancellationEvent = async (_, token) =>
            {
                token.Register(() => cancelledTcs.TrySetResult());
                await Hang();
            };
            nexus.ServerTaskEvent = async _ => await Hang();
            nexus.OnDisconnectedEvent = _ =>
            {
                disconnectedTcs.TrySetResult();
                return ValueTask.CompletedTask;
            };
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var invocationTask = withCancellationToken
            ? client.Proxy.ServerTaskWithCancellation(CancellationToken.None).AsTask()
            : client.Proxy.ServerTask().AsTask();
        // Observe the expected client-side failure once the connection drops.
        _ = invocationTask.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        await startedTcs.Task.Timeout(1);

        await client.DisconnectAsync().Timeout(1);
        await disconnectedTcs.Task.Timeout(1);

        if (withCancellationToken)
            await cancelledTcs.Task.AssertTimeout(0.5);
        await methodExitedTcs.Task.AssertTimeout(0.5);

        // Cleanup: release the orphaned invocation.
        hangTcs.TrySetResult();
        await methodExitedTcs.Task.Timeout(1);
    }
}
