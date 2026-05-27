using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

// Temporary 14b shape: existing showcase tests rewritten against the new Editor* types so
// the suite stays green between 14b (rename) and 14c (replace this file with
// EditorAppShowcaseTests). Will be deleted in 14c.
internal class HarnessShowcaseTests
{
    private static Task<NexusTestHost<EditorServerNexus, EditorServerNexus.ClientProxy, EditorClientNexus, EditorClientNexus.ServerProxy>> CreateHost()
        => NexusTestHost.CreateAsync<
            EditorServerNexus, EditorServerNexus.ClientProxy,
            EditorClientNexus, EditorClientNexus.ServerProxy>();

    [SetUp]
    public void ResetEditorState() => EditorServerNexus.ResetAll();

    [Test]
    public async Task Groups_EmptyGroup_HasNoMembers()
    {
        await using var host = await CreateHost();
        Assert.That(host.Groups["doc-design.md"].Members, Is.Empty);
        Assert.That(host.Groups["doc-design.md"].Count, Is.EqualTo(0));
    }

    [Test]
    public async Task Groups_ReflectMembershipAfterJoin()
    {
        await using var host = await CreateHost();

        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var c2 = await host.ConnectAsAsync(TestIdentity.Of("bob", "Write"));
        var c3 = await host.ConnectAsAsync(TestIdentity.Of("carol", "Write"));

        await c1.Server.OpenDocument("design.md");
        await c2.Server.OpenDocument("design.md");
        await c3.Server.OpenDocument("recipe.txt");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(host.Groups["doc-design.md"].Count, Is.EqualTo(2));
        Assert.That(host.Groups["doc-recipe.txt"].Count, Is.EqualTo(1));
        Assert.That(host.Groups["doc-nope"].Count, Is.EqualTo(0));

        var editorIds = host.Groups["doc-design.md"].Members;
        Assert.That(editorIds.Length, Is.EqualTo(2));
        Assert.That(editorIds.Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task GroupBroadcast_DeliversToMembers()
    {
        await using var host = await CreateHost();

        var c1 = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var c2 = await host.ConnectAsAsync(TestIdentity.Of("bob", "Write"));
        var c3 = await host.ConnectAsAsync(TestIdentity.Of("carol", "Write"));

        await c1.Server.OpenDocument("design.md");
        await c2.Server.OpenDocument("design.md");
        await c3.Server.OpenDocument("recipe.txt");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await c1.Server.SaveDraft("design.md", "hello-editors");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        c1.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "hello-editors"));
        c2.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "hello-editors"));
        c3.AssertNotReceived<IEditorClientNexus>(n => n.DraftSaved(Arg.Any<string>(), Arg.Any<string>()));
    }
}
