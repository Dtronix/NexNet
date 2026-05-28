using System;
using System.Threading.Tasks;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class ClientAssertionTests
{
    private static Task<NexusTestHost<EditorServerNexus, EditorServerNexus.ClientProxy, EditorClientNexus, EditorClientNexus.ServerProxy>> CreateHost()
        => NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>();

    [SetUp]
    public void ResetEditorState() => EditorServerNexus.ResetAll();

    [Test]
    public async Task AssertReceived_OnSpecificClient_Succeeds()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var c2 = await host.ConnectAsAsync(TestIdentity.Of("bob", "Write"));

        await c1.Server.OpenDocument("design.md");
        await c2.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // SaveDraft uses Group (not GroupExceptCaller), so both c1 and c2 receive DraftSaved.
        await c1.Server.SaveDraft("design.md", "v1");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        c1.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "v1"));
        c2.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "v1"));
    }

    [Test]
    public async Task AssertNotReceived_OnNonMember_Succeeds()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var outsider = await host.ConnectAsAsync(TestIdentity.Of("outsider", "Write"));

        await alice.Server.OpenDocument("design.md");
        // outsider intentionally not in the design.md group
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await alice.Server.SaveDraft("design.md", "members-only");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        alice.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "members-only"));
        outsider.AssertNotReceived<IEditorClientNexus>(n => n.DraftSaved(Arg.Any<string>(), Arg.Any<string>()));
    }

    [Test]
    public async Task AssertReceived_TimesMismatch_Throws()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        await c1.Server.OpenDocument("design.md");
        await c1.Server.SaveDraft("design.md", "once");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Throws<NexusAssertionException>(
            () => c1.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "once"), times: 2));
    }

    [Test]
    public async Task WaitFor_OnClient_CompletesWhenMatchArrives()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        await c1.Server.OpenDocument("design.md");

        // Start WaitFor BEFORE the broadcast so it has to wait for arrival.
        var wait = c1.WaitFor<IEditorClientNexus>(n => n.DraftSaved(Arg.Any<string>(), Arg.Any<string>()), TimeSpan.FromSeconds(3));
        await c1.Server.SaveDraft("design.md", "delayed");
        await wait;  // should complete within timeout
    }

    [Test]
    public async Task WaitFor_Timeout_Throws()
    {
        await using var host = await CreateHost();
        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await c1.WaitFor<IEditorClientNexus>(
                n => n.DraftSaved("never", "never"),
                TimeSpan.FromMilliseconds(200)));
    }
}
