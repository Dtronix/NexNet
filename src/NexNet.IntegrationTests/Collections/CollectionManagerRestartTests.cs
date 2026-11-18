using NexNet.Collections;
using NexNet.Invocation;
using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// A stop does not wait for the collection broadcaster loops to unwind, so a quick restart used to find them still
/// running and throw "Broadcaster is already running." (S08).
/// </summary>
internal class CollectionManagerRestartTests : NexusCollectionBaseTests
{
    private const int Cycles = 200;

    [Test]
    public void CollectionManager_FastStopStartCycles_DoNotThrow()
    {
        var manager = new NexusCollectionManager(null, true, TimeProvider.System);
        manager.ConfigureList<int>(0, NexusCollectionMode.BiDirectional);
        manager.ConfigureList<int>(1, NexusCollectionMode.ServerToClient);
        manager.CompleteConfigure();

        Assert.DoesNotThrow(() =>
        {
            for (var i = 0; i < Cycles; i++)
            {
                manager.Stop();
                manager.Start();
            }
        });

        manager.Stop();
    }

    [Test]
    public async Task Server_FastStopStartCycles_CollectionsWorkAfterLastStart()
    {
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(Type.Uds),
            CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(1);
        for (var i = 0; i < Cycles; i++)
        {
            await server.StopAsync().Timeout(1);
            await server.StartAsync().Timeout(1);
        }

        await client.ConnectAsync().Timeout(1);
        var serverNexus = server.NexusCreatedQueue.First();
        Assert.That(await client.Proxy.IntListBi.EnableAsync().Timeout(1), Is.True);
        await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(2).Timeout(1);

        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([1, 2]));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2]));
        });
    }
}
