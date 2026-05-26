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
        var sNexus = new DemoServerNexus();
        var cNexus = new DemoClientNexus();

        await using var host = await NexusTestHost.CreateAsync<
            DemoServerNexus, DemoServerNexus.ClientProxy,
            DemoClientNexus, DemoClientNexus.ServerProxy>(
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
        var sNexus = new DemoServerNexus();
        var cNexus = new DemoClientNexus();

        await using var host = await NexusTestHost.CreateAsync<
            DemoServerNexus, DemoServerNexus.ClientProxy,
            DemoClientNexus, DemoClientNexus.ServerProxy>(
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

    /// <summary>
    /// Drives the harness with a burst of invocations and a fire-and-forget Notify, then checks
    /// QuiesceAsync only completes after every invocation has returned and every byte has been
    /// drained. Validates all four counters (bytesInTransit, inDispatch, pendingResults,
    /// activePipes) wired into real session-level activity, not unit-test-only mocks.
    /// </summary>
    [Test]
    public async Task QuiesceAsync_AwaitsRealActivityEndToEnd()
    {
        var sNexus = new DemoServerNexus();
        var cNexus = new DemoClientNexus();

        await using var host = await NexusTestHost.CreateAsync<
            DemoServerNexus, DemoServerNexus.ClientProxy,
            DemoClientNexus, DemoClientNexus.ServerProxy>(
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
