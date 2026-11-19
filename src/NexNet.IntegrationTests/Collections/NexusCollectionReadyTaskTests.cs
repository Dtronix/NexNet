using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Pipes.Broadcast;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A client collection that disconnects before its first synchronization fails its ReadyTask instead of leaving it
/// pending forever, and a new enable gets a fresh ReadyTask (S15).
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
            Assert.That(ready.IsCompleted, Is.False);

            await client.DisconnectAsync().Timeout(1);

            await AssertThrows<InvalidOperationException>(() => ready.Timeout(1));
            Assert.That(ready.Exception!.InnerException!.Message,
                Does.Contain("disconnected before its initial synchronization"));
            Assert.That(await enable.Timeout(1), Is.False);

            // ReadyTask keeps the failure instead of reporting the collection as ready.
            Assert.That(list.ReadyTask.IsFaulted, Is.True);
        }
        finally
        {
            release.Set();
            broadcaster.ProcessTestHook = null;
        }

        // The held synchronization finishing late does not mark the collection connected.
        await Task.Delay(50);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Disconnected));

        // A new enable on a new session gets a fresh ReadyTask.
        await client.ConnectAsync().Timeout(1);
        list = client.Proxy.IntListBi;
        Assert.That(await list.EnableAsync().Timeout(1), Is.True);
        await list.ReadyTask.Timeout(1);
        Assert.That(list.State, Is.EqualTo(NexusCollectionState.Connected));
        Assert.That(list, Is.EquivalentTo([7]));
    }
}
