using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class HarnessShowcaseTests
{
    private static Task<NexusTestHost<DemoServerNexus, DemoServerNexus.ClientProxy, DemoClientNexus, DemoClientNexus.ServerProxy>> CreateHost()
        => NexusTestHost.CreateAsync<
            DemoServerNexus, DemoServerNexus.ClientProxy,
            DemoClientNexus, DemoClientNexus.ServerProxy>();

    [Test]
    public async Task Groups_EmptyGroup_HasNoMembers()
    {
        await using var host = await CreateHost();
        Assert.That(host.Groups["editors"].Members, Is.Empty);
        Assert.That(host.Groups["editors"].Count, Is.EqualTo(0));
    }

    [Test]
    public async Task Groups_ReflectMembershipAfterJoin()
    {
        await using var host = await CreateHost();

        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var c2 = await host.ConnectAsAsync(TestIdentity.Of("bob"));
        var c3 = await host.ConnectAsAsync(TestIdentity.Of("carol"));

        await c1.Server.JoinGroup("editors");
        await c2.Server.JoinGroup("editors");
        await c3.Server.JoinGroup("readers");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(host.Groups["editors"].Count, Is.EqualTo(2));
        Assert.That(host.Groups["readers"].Count, Is.EqualTo(1));
        Assert.That(host.Groups["nope"].Count, Is.EqualTo(0));

        // The session ids in editors should be a subset of the connected clients' session ids.
        var editorIds = host.Groups["editors"].Members;
        Assert.That(editorIds.Length, Is.EqualTo(2));
        Assert.That(editorIds.Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task GroupBroadcast_DeliversToMembers()
    {
        await using var host = await CreateHost();

        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var c2 = await host.ConnectAsAsync(TestIdentity.Of("bob"));
        var c3 = await host.ConnectAsAsync(TestIdentity.Of("carol"));

        await c1.Server.JoinGroup("editors");
        await c2.Server.JoinGroup("editors");
        await c3.Server.JoinGroup("readers");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await c1.Server.BroadcastToGroup("editors", "hello-editors");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(c1.Nexus.LastMessage, Is.EqualTo("hello-editors"));
        Assert.That(c2.Nexus.LastMessage, Is.EqualTo("hello-editors"));
        Assert.That(c3.Nexus.LastMessage, Is.Null);
    }
}
