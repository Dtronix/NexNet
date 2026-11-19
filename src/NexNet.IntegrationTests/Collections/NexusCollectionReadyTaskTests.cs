using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes.Broadcast;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A client collection that disconnects before its first synchronization fails its ReadyTask instead of leaving it
/// pending forever, and a new enable gets a fresh ReadyTask. EnableAsync finishes its run's cleanup before it
/// reports a disconnect, and a late cleanup leaves the next run alone (S15).
/// </summary>
internal class NexusCollectionReadyTaskTests : NexusCollectionBaseTests
{
    [Test]
    public async Task SessionDisconnectsMidReset_ReadyTaskFails()
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var serverNexus = server.NexusCreatedQueue.First();
        await serverNexus.IntListBi.AddAsync(7).Timeout(1);
        var list = client.Proxy.IntListBi;

        // Hold the initial synchronization at its first message (the reset start) until the session is gone.
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var broadcaster = (NexusBroadcastClient<INexusCollectionListMessage>)(object)list;
        var armed = 1;
        broadcaster.ProcessTestHook = _ =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1)
                return;
            resetStarted.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        try
        {
            var enable = list.EnableAsync().AsTask();
            await resetStarted.Task.Timeout(1);

            var ready = list.ReadyTask;
            await Assert.That(ready.IsCompleted).IsFalse();

            await client.DisconnectAsync().Timeout(1);

            await AssertThrows<InvalidOperationException>(() => ready.Timeout(1));
            await Assert.That(ready.Exception!.InnerException!.Message)
                .Contains("disconnected before its initial synchronization");
            await Assert.That(await enable.Timeout(1)).IsFalse();

            // ReadyTask keeps the failure instead of reporting the collection as ready.
            await Assert.That(list.ReadyTask.IsFaulted).IsTrue();
        }
        finally
        {
            release.Set();
            broadcaster.ProcessTestHook = null;
        }

        // The held synchronization finishing late does not mark the collection connected.
        await Task.Delay(50);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);

        // A new enable on a new session gets a fresh ReadyTask.
        await client.ConnectAsync().Timeout(1);
        list = client.Proxy.IntListBi;
        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await list.ReadyTask.Timeout(1);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected);
        await Assert.That(list).IsEquivalentTo([7]);
    }

    [Test]
    public async Task SessionDisconnectsDuringEnable_EnableFinishesCleanupBeforeReturning()
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        await server.NexusCreatedQueue.First().IntListBi.AddAsync(7).Timeout(1);
        var list = client.Proxy.IntListBi;
        var broadcaster = (NexusBroadcastClient<INexusCollectionListMessage>)(object)list;

        // Hold the initial synchronization, then hold the disconnect cleanup it triggers.
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReset = new ManualResetEventSlim();
        using var releaseCleanup = new ManualResetEventSlim();
        var resetArmed = 1;
        broadcaster.ProcessTestHook = _ =>
        {
            if (Interlocked.Exchange(ref resetArmed, 0) != 1)
                return;
            resetStarted.TrySetResult();
            releaseReset.Wait(TimeSpan.FromSeconds(5));
        };
        broadcaster.DisconnectCleanupTestHook = () =>
        {
            cleanupStarted.TrySetResult();
            releaseCleanup.Wait(TimeSpan.FromSeconds(5));
        };

        try
        {
            var enable = list.EnableAsync().AsTask();
            await resetStarted.Task.Timeout(1);

            await client.DisconnectAsync().Timeout(1);
            await cleanupStarted.Task.Timeout(1);

            // The pipe is closed, but EnableAsync waits for its run's cleanup before it reports the failure.
            await Task.Delay(100);
            await Assert.That(enable.IsCompleted).IsFalse();

            releaseCleanup.Set();
            await Assert.That(await enable.Timeout(1)).IsFalse();
            await Assert.That(list.DisabledTask.IsCompleted).IsTrue();
            await Assert.That(list.ReadyTask.IsFaulted).IsTrue();
        }
        finally
        {
            releaseReset.Set();
            releaseCleanup.Set();
            broadcaster.ProcessTestHook = null;
            broadcaster.DisconnectCleanupTestHook = null;
        }

        // An immediate retry is a new enable against the closed session: it cannot report the collection enabled.
        bool? retried;
        try
        {
            retried = await list.EnableAsync().Timeout(1);
        }
        catch (Exception)
        {
            retried = null;
        }

        await Assert.That(retried).IsNotEqualTo(true);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);
    }

    [Test]
    public async Task LateDisconnectCleanup_LeavesTheNextEnableRunIntact()
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);
        var serverNexus = server.NexusCreatedQueue.First();
        var list = client.Proxy.IntListBi;
        var broadcaster = (NexusBroadcastClient<INexusCollectionListMessage>)(object)list;

        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await list.ReadyTask.Timeout(1);

        // Hold the first run's cleanup at the point where the collection already accepts a new enable.
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCleanup = new ManualResetEventSlim();
        var armed = 1;
        broadcaster.DisconnectCleanupTestHook = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1)
                return;
            cleanupStarted.TrySetResult();
            releaseCleanup.Wait(TimeSpan.FromSeconds(5));
        };

        Task disableFirst;
        Task readySecond;
        Task disabledSecond;
        try
        {
            disableFirst = list.DisableAsync().AsTask();
            await cleanupStarted.Task.Timeout(1);

            // A second run starts while the first one is still cleaning up.
            await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
            await list.ReadyTask.Timeout(1);
            readySecond = list.ReadyTask;
            disabledSecond = list.DisabledTask;
        }
        finally
        {
            releaseCleanup.Set();
        }

        await disableFirst.Timeout(1);
        broadcaster.DisconnectCleanupTestHook = null;

        // The first run's cleanup left the second run's tasks, state and stop token alone.
        await Assert.That(ReferenceEquals(list.DisabledTask, disabledSecond)).IsTrue();
        await Assert.That(disabledSecond.IsCompleted).IsFalse();
        await Assert.That(ReferenceEquals(list.ReadyTask, readySecond)).IsTrue();
        await Assert.That(readySecond.IsCompletedSuccessfully).IsTrue();
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected);

        await Assert.That(await list.AddAsync(1).Timeout(1)).IsTrue();
        await Assert.That(serverNexus.IntListBi).IsEquivalentTo([1]);

        // The second run still disables normally.
        await list.DisableAsync().Timeout(1);
        await disabledSecond.Timeout(1);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);
    }
}
