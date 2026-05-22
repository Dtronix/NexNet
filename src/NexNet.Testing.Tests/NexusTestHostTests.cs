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
}
