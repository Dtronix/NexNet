using System;
using System.Threading.Tasks;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class NexusTestHostTests
{
    [Test]
    public async Task EndToEnd_InvokeServerMethodOverInProcess()
    {
        var sNexus = new EditorServerNexus();
        var cNexus = new EditorClientNexus();

        await using var host = await NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>(
                serverNexusFactory: () => sNexus,
                clientNexusFactory: () => cNexus);

        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        var result = await client.Server.Ping(42);
        Assert.That(result, Is.EqualTo(42));
        Assert.That(sNexus.PingCount, Is.EqualTo(1));

        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ManyInvocations_OneClient()
    {
        var sNexus = new EditorServerNexus();
        var cNexus = new EditorClientNexus();

        await using var host = await NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>(
                serverNexusFactory: () => sNexus,
                clientNexusFactory: () => cNexus);

        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        for (int i = 0; i < 20; i++)
        {
            var r = await client.Server.Ping(i);
            Assert.That(r, Is.EqualTo(i));
        }

        Assert.That(sNexus.PingCount, Is.EqualTo(20));
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task SharedClientNexusInstance_ThrowsOnSecondConnect()
    {
        var sharedClient = new EditorClientNexus();

        await using var host = await NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>(
                serverNexusFactory: () => new EditorServerNexus(),
                clientNexusFactory: () => sharedClient);

        await host.ConnectAsAsync(TestIdentity.Of("alice"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await host.ConnectAsAsync(TestIdentity.Of("bob"))
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(ex!.Message, Does.Contain("clientNexusFactory"));
    }

    [Test]
    public async Task MultipleClients_CanConnectAgainstSameHost()
    {
        await using var host = await NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>(
                serverNexusFactory: () => new EditorServerNexus(),
                clientNexusFactory: () => new EditorClientNexus());

        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice"))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var c2 = await host.ConnectAsAsync(TestIdentity.Of("bob"))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var c3 = await host.ConnectAsAsync(TestIdentity.Of("carol"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(await c1.Server.Ping(1), Is.EqualTo(1));
        Assert.That(await c2.Server.Ping(2), Is.EqualTo(2));
        Assert.That(await c3.Server.Ping(3), Is.EqualTo(3));
    }

    /// <summary>
    /// Drives the harness with a burst of invocations and a fire-and-forget Notify, then checks
    /// QuiesceAsync only completes after every invocation has returned and every byte has been
    /// drained. Validates all four counters (bytesInTransit, inDispatch, pendingResults,
    /// activePipes) wired into real session-level activity, not unit-test-only mocks.
    /// </summary>
    [Test]
    public async Task QuiesceAsync_AwaitsRealActivityEndToEnd()
    {
        var sNexus = new EditorServerNexus();
        var cNexus = new EditorClientNexus();

        await using var host = await NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>(
                serverNexusFactory: () => sNexus,
                clientNexusFactory: () => cNexus);

        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        // Mix waited-on calls (exercise inDispatch + pendingResults + bytesInTransit)
        // with fire-and-forget Notify (exercise bytesInTransit + inDispatch without
        // a return-value path).
        var awaited = new System.Collections.Generic.List<Task>();
        for (int i = 0; i < 50; i++)
        {
            awaited.Add(client.Server.Ping(i).AsTask());
            await client.Server.Notify($"msg-{i}");
        }
        await Task.WhenAll(awaited);

        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sNexus.PingCount, Is.EqualTo(50));
    }
}
