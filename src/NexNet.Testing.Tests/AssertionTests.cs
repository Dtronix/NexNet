using System;
using System.Threading.Tasks;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class AssertionTests
{
    [SetUp]
    public void ResetEditorState() => EditorServerNexus.ResetAll();

    private async Task<(NexusTestHost<EditorServerNexus, EditorServerNexus.ClientProxy, EditorClientNexus, EditorClientNexus.ServerProxy> host,
            NexusTestClient<EditorClientNexus, EditorClientNexus.ServerProxy> client,
            EditorServerNexus server)>
        SetupAsync()
    {
        var sNexus = new EditorServerNexus();
        var host = await NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>(
                () => sNexus,
                () => new EditorClientNexus());
        var client = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        return (host, client, sNexus);
    }

    [Test]
    public async Task AssertReceived_PassesAfterInvocation()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(42);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IEditorServerNexus>(n => n.Ping(42));
    }

    [Test]
    public async Task AssertReceived_WildcardArg()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(99);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IEditorServerNexus>(n => n.Ping(Arg.Any<int>()));
    }

    [Test]
    public async Task AssertReceived_PredicateArg()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(150);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IEditorServerNexus>(n => n.Ping(Arg.Is<int>(x => x > 100)));
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
            () => host.AssertReceived<IEditorServerNexus>(n => n.Ping(Arg.Any<int>()), times: 5));
    }

    [Test]
    public async Task AssertNotReceived_PassesWhenAbsent()
    {
        var (host, _, _) = await SetupAsync();
        await using var _host = host;
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertNotReceived<IEditorServerNexus>(n => n.Ping(Arg.Any<int>()));
    }

    [Test]
    public async Task AssertNotReceived_ThrowsWhenPresent()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Ping(7);
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<NexusAssertionException>(
            () => host.AssertNotReceived<IEditorServerNexus>(n => n.Ping(Arg.Any<int>())));
    }

    [Test]
    public async Task WaitFor_CompletesOnArrival()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        var waiter = host.WaitFor<IEditorServerNexus>(n => n.Ping(Arg.Any<int>()), TimeSpan.FromSeconds(2));
        await client.Server.Ping(5);
        await waiter;
    }

    [Test]
    public async Task WaitFor_TimesOut()
    {
        var (host, _, _) = await SetupAsync();
        await using var _host = host;

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await host.WaitFor<IEditorServerNexus>(n => n.Ping(Arg.Any<int>()), TimeSpan.FromMilliseconds(200)));
    }

    [Test]
    public async Task AssertReceived_StringArg_MatchesExactly()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.Notify("hello");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IEditorServerNexus>(n => n.Notify("hello"));
        host.AssertNotReceived<IEditorServerNexus>(n => n.Notify("goodbye"));
    }

    [Test]
    public async Task AssertReceived_MultiArg_AllMustMatch()
    {
        var (host, client, _) = await SetupAsync();
        await using var _host = host;

        await client.Server.SaveDraft("design.md", "v1");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IEditorServerNexus>(n => n.SaveDraft("design.md", "v1"));
        host.AssertReceived<IEditorServerNexus>(n => n.SaveDraft("design.md", Arg.Any<string>()));
        host.AssertReceived<IEditorServerNexus>(n => n.SaveDraft(Arg.Any<string>(), Arg.Any<string>()));
        host.AssertNotReceived<IEditorServerNexus>(n => n.SaveDraft("design.md", "v2"));
        host.AssertNotReceived<IEditorServerNexus>(n => n.SaveDraft("recipe.txt", "v1"));
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
            () => host.AssertReceived<IEditorServerNexus>(n => n.Notify("gamma")));

        // The diagnostic must include the actual recorded arg values, not just method ids.
        Assert.That(ex!.Message, Does.Contain("alpha"));
        Assert.That(ex.Message, Does.Contain("beta"));
        Assert.That(ex.Message, Does.Contain("Notify"));
    }
}
