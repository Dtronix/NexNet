using System;
using System.Threading.Tasks;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class AssertionTests
{
    private async Task<(NexusTestHost<DemoServerNexus, DemoServerNexus.ClientProxy, DemoClientNexus, DemoClientNexus.ServerProxy> host,
            NexusTestClient<DemoClientNexus, DemoClientNexus.ServerProxy> client,
            DemoServerNexus server)>
        SetupAsync()
    {
        var sNexus = new DemoServerNexus();
        var host = await NexusTestHost.CreateAsync<
            DemoServerNexus, DemoServerNexus.ClientProxy,
            DemoClientNexus, DemoClientNexus.ServerProxy>(
                () => sNexus,
                () => new DemoClientNexus());
        var client = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        return (host, client, sNexus);
    }

    [Test]
    public async Task AssertReceived_PassesAfterInvocation()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(42);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IDemoServerNexus>(n => n.Ping(42));
    }

    [Test]
    public async Task AssertReceived_WildcardArg()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(99);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IDemoServerNexus>(n => n.Ping(Arg.Any<int>()));
    }

    [Test]
    public async Task AssertReceived_PredicateArg()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(150);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IDemoServerNexus>(n => n.Ping(Arg.Is<int>(x => x > 100)));
    }

    [Test]
    public async Task AssertReceived_TimesMismatch_Throws()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(1);
        await client.Server.Ping(2);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<NexusAssertionException>(
            () => host.AssertReceived<IDemoServerNexus>(n => n.Ping(Arg.Any<int>()), times: 5));
    }

    [Test]
    public async Task AssertNotReceived_PassesWhenAbsent()
    {
        var (host, _, _) = await SetupAsync();
        await using var _host = host;
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertNotReceived<IDemoServerNexus>(n => n.Ping(Arg.Any<int>()));
    }

    [Test]
    public async Task AssertNotReceived_ThrowsWhenPresent()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(7);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<NexusAssertionException>(
            () => host.AssertNotReceived<IDemoServerNexus>(n => n.Ping(Arg.Any<int>())));
    }

    [Test]
    public async Task WaitFor_CompletesOnArrival()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        var waiter = host.WaitFor<IDemoServerNexus>(n => n.Ping(Arg.Any<int>()), TimeSpan.FromSeconds(2));
        await client.Server.Ping(5);
        await waiter;
    }

    [Test]
    public async Task WaitFor_TimesOut()
    {
        var (host, _, _) = await SetupAsync();
        await using var _host = host;

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await host.WaitFor<IDemoServerNexus>(n => n.Ping(Arg.Any<int>()), TimeSpan.FromMilliseconds(200)));
    }

    [Test]
    public async Task AssertReceived_StringArg_MatchesExactly()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Notify("hello");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IDemoServerNexus>(n => n.Notify("hello"));
        host.AssertNotReceived<IDemoServerNexus>(n => n.Notify("goodbye"));
    }

    [Test]
    public async Task AssertReceived_MultiArg_AllMustMatch()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.BroadcastToGroup("editors", "draft-saved");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IDemoServerNexus>(n => n.BroadcastToGroup("editors", "draft-saved"));
        host.AssertReceived<IDemoServerNexus>(n => n.BroadcastToGroup("editors", Arg.Any<string>()));
        host.AssertReceived<IDemoServerNexus>(n => n.BroadcastToGroup(Arg.Any<string>(), Arg.Any<string>()));
        host.AssertNotReceived<IDemoServerNexus>(n => n.BroadcastToGroup("editors", "wrong-text"));
        host.AssertNotReceived<IDemoServerNexus>(n => n.BroadcastToGroup("readers", "draft-saved"));
    }

    [Test]
    public async Task AssertReceived_MismatchDiagnostic_IncludesRecordedArgs()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Notify("alpha");
        await client.Server.Notify("beta");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var ex = Assert.Throws<NexusAssertionException>(
            () => host.AssertReceived<IDemoServerNexus>(n => n.Notify("gamma")));

        // The diagnostic must include the actual recorded arg values, not just method ids.
        Assert.That(ex!.Message, Does.Contain("alpha"));
        Assert.That(ex.Message, Does.Contain("beta"));
        Assert.That(ex.Message, Does.Contain("Notify"));
    }
}
