using System;
using System.Threading.Tasks;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class ClientAssertionTests
{
    private static Task<NexusTestHost<DemoServerNexus, DemoServerNexus.ClientProxy, DemoClientNexus, DemoClientNexus.ServerProxy>> CreateHost()
        => NexusTestHost.CreateAsync<
            DemoServerNexus, DemoServerNexus.ClientProxy,
            DemoClientNexus, DemoClientNexus.ServerProxy>();

    [Test]
    public async Task AssertReceived_OnSpecificClient_Succeeds()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var c2 = await host.ConnectAsAsync(TestIdentity.Of("bob"));

        await c1.Server.JoinGroup("editors");
        await c2.Server.JoinGroup("editors");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // Broadcast from alice → both members of "editors" (c1 and c2) get ReceiveBroadcast.
        await c1.Server.BroadcastToGroup("editors", "hi-editors");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        c1.AssertReceived<IDemoClientNexus>(n => n.ReceiveBroadcast("hi-editors"));
        c2.AssertReceived<IDemoClientNexus>(n => n.ReceiveBroadcast("hi-editors"));
    }

    [Test]
    public async Task AssertNotReceived_OnNonMember_Succeeds()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var outsider = await host.ConnectAsAsync(TestIdentity.Of("outsider"));

        await alice.Server.JoinGroup("editors");
        // outsider intentionally not in group
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await alice.Server.BroadcastToGroup("editors", "members-only");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        alice.AssertReceived<IDemoClientNexus>(n => n.ReceiveBroadcast("members-only"));
        outsider.AssertNotReceived<IDemoClientNexus>(n => n.ReceiveBroadcast("members-only"));
    }

    [Test]
    public async Task AssertReceived_TimesMismatch_Throws()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        await c1.Server.JoinGroup("editors");
        await c1.Server.BroadcastToGroup("editors", "once");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<NexusAssertionException>(
            () => c1.AssertReceived<IDemoClientNexus>(n => n.ReceiveBroadcast("once"), times: 2));
    }

    [Test]
    public async Task WaitFor_OnClient_CompletesWhenMatchArrives()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        await c1.Server.JoinGroup("editors");

        // Start WaitFor BEFORE the broadcast so it has to wait for arrival.
        var wait = c1.WaitFor<IDemoClientNexus>(n => n.ReceiveBroadcast(Arg.Any<string>()), TimeSpan.FromSeconds(3));
        await c1.Server.BroadcastToGroup("editors", "delayed");
        await wait;  // should complete within timeout
    }

    [Test]
    public async Task WaitFor_Timeout_Throws()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await c1.WaitFor<IDemoClientNexus>(
                n => n.ReceiveBroadcast("never"),
                TimeSpan.FromMilliseconds(200)));
    }
}
