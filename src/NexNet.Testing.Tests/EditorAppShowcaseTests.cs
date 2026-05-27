using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Invocation;
using NexNet.Testing.Streaming;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

/// <summary>
/// End-to-end showcase of the NexNet.Testing harness driven through the editor-app demo
/// nexus. Each test exercises one observable harness capability via a natural business
/// verb on <see cref="IEditorServerNexus"/> rather than synthetic passthrough methods.
/// </summary>
internal class EditorAppShowcaseTests
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
    public async Task SaveDraft_BroadcastsToDocGroup_WithAuthorIdentity()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob", "Write"));
        var carol = await host.ConnectAsAsync(TestIdentity.Of("carol", "Write"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await carol.Server.OpenDocument("recipe.txt");

        await alice.Server.SaveDraft("design.md", "v1");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // SaveDraft uses Group (caller-inclusive) → alice + bob receive DraftSaved.
        alice.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "v1"));
        bob.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "v1"));
        carol.AssertNotReceived<IEditorClientNexus>(
            n => n.DraftSaved(Arg.Any<string>(), Arg.Any<string>()));
    }

    [Test]
    public async Task LeaveDocument_NotifiesOthersExceptCaller()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob"));
        var carol = await host.ConnectAsAsync(TestIdentity.Of("carol"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await carol.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        await bob.Server.LeaveDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // GroupExceptCaller → alice + carol see bob leave; bob does not see his own EditorLeft.
        alice.AssertReceived<IEditorClientNexus>(n => n.EditorLeft("bob"));
        carol.AssertReceived<IEditorClientNexus>(n => n.EditorLeft("bob"));
        bob.AssertNotReceived<IEditorClientNexus>(n => n.EditorLeft("bob"));
    }

    [Test]
    public async Task Whisper_DeliveredToTargetOnly()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob"));
        var carol = await host.ConnectAsAsync(TestIdentity.Of("carol"));

        await alice.Server.Whisper(bob.Nexus.Context.Id, "hi-bob");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        bob.AssertReceived<IEditorClientNexus>(n => n.WhisperReceived("alice", "hi-bob"));
        carol.AssertNotReceived<IEditorClientNexus>(
            n => n.WhisperReceived(Arg.Any<string>(), Arg.Any<string>()));
        alice.AssertNotReceived<IEditorClientNexus>(
            n => n.WhisperReceived(Arg.Any<string>(), Arg.Any<string>()));
    }

    [Test]
    public async Task BroadcastSystemAnnouncement_AsNonAdmin_Throws()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));

        Assert.ThrowsAsync<ProxyUnauthorizedException>(
            async () => await alice.Server.BroadcastSystemAnnouncement("hello"));
    }

    [Test]
    public async Task BroadcastSystemAnnouncement_AsAdmin_DeliversToAll()
    {
        await using var host = await CreateHost();
        var admin = await host.ConnectAsAsync(TestIdentity.Of("root", "Admin"));
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob", "Read"));

        await admin.Server.BroadcastSystemAnnouncement("server-restart-in-5");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        admin.AssertReceived<IEditorClientNexus>(n => n.SystemAnnouncement("server-restart-in-5"));
        alice.AssertReceived<IEditorClientNexus>(n => n.SystemAnnouncement("server-restart-in-5"));
        bob.AssertReceived<IEditorClientNexus>(n => n.SystemAnnouncement("server-restart-in-5"));
    }

    [Test]
    public async Task SaveDraft_AsReader_Throws()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Read"));

        Assert.ThrowsAsync<ProxyUnauthorizedException>(
            async () => await alice.Server.SaveDraft("design.md", "v1"));
    }

    [Test]
    public async Task ListActiveEditors_ReturnsNames()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob"));
        var carol = await host.ConnectAsAsync(TestIdentity.Of("carol"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await carol.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var names = await alice.Server.ListActiveEditors("design.md", CancellationToken.None);

        Assert.That(names, Is.EquivalentTo(new[] { "alice", "bob", "carol" }));
    }

    [Test]
    public async Task ListActiveEditors_CancelledToken_PropagatesAndThrows()
    {
        // Client-side CT fires partway through the server's `await Task.Delay(50, ct)` in
        // ListActiveEditors. Framework propagates the cancel signal to the server-side CT,
        // which causes the delay to throw. Pre-cancelled tokens are not short-circuited by
        // the proxy — the cancel signal is only sent when the client-side CT FIRES.
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        Assert.ThrowsAsync<TaskCanceledException>(
            async () => await alice.Server.ListActiveEditors("design.md", cts.Token));
    }

    [Test]
    public async Task UploadAttachment_StreamsBytes_ServerAppendsToDoc()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        var payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

        await using var pipe = alice.CreatePipe();
        var serverCall = alice.Server.UploadAttachment("design.md", pipe).AsTask();
        await pipe.PipeUploadAsync(payload);
        await serverCall.WaitAsync(TimeSpan.FromSeconds(2));
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(EditorServerNexus.Documents.TryGetValue("design.md", out var doc), Is.True);
        Assert.That(doc!.Attachment, Is.EqualTo(payload));
    }

    [Test]
    public async Task StreamEdits_AllOpsCollected()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        var ops = Enumerable.Range(0, 10)
            .Select(i => new EditOp(i, $"text-{i}"))
            .ToArray();

        var channel = alice.Nexus.Context.CreateChannel<EditOp>();
        var serverCall = alice.Server.StreamEdits("design.md", channel).AsTask();
        var writer = await channel.GetWriterAsync();
        foreach (var op in ops)
            await writer.WriteAsync(op);
        await writer.CompleteAsync();
        await serverCall.WaitAsync(TimeSpan.FromSeconds(2));
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(EditorServerNexus.Documents.TryGetValue("design.md", out var doc), Is.True);
        Assert.That(doc!.Edits.ToArray(), Is.EqualTo(ops));
    }
}
