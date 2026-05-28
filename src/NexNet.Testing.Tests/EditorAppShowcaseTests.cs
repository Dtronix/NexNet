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
        // Client-side CT fires partway through the server's `await Task.Delay(200, ct)` in
        // ListActiveEditors (the delay is guarded on CanBeCanceled, so only cancellable
        // callers pay it). Framework propagates the cancel signal to the server-side CT,
        // which causes the delay to throw. Pre-cancelled tokens are not short-circuited by
        // the proxy — the cancel signal is only sent when the client-side CT FIRES. The
        // assertion uses OperationCanceledException (the base) rather than TaskCanceledException
        // so the test pins the semantic, not the concrete subtype.
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        Assert.That(
            async () => await alice.Server.ListActiveEditors("design.md", cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
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

        await using var channel = alice.Nexus.Context.CreateChannel<EditOp>();
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

    [Test]
    public async Task WaitFor_DraftSaved_ResolvesWhenInvoked()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob", "Write"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // Producer runs in the background AFTER a small delay so the waiter actually
        // observes the async-arrival path, not the "already recorded" fast path.
        var waiter = alice.WaitFor<IEditorClientNexus>(
            n => n.DraftSaved("bob", "v1"), TimeSpan.FromSeconds(3));
        var producer = Task.Run(async () =>
        {
            await Task.Delay(100);
            await bob.Server.SaveDraft("design.md", "v1");
        });
        await waiter;
        await producer;
    }

    [Test]
    public async Task WaitFor_Timeout_Throws()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await alice.WaitFor<IEditorClientNexus>(
                n => n.DraftSaved("never", "never"),
                TimeSpan.FromMilliseconds(250)));
    }

    [Test]
    public async Task MixedTraffic_QuiesceAsync_WaitsForEverything()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob", "Write"));
        var carol = await host.ConnectAsAsync(TestIdentity.Of("carol", "Write"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await carol.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var attachment = new byte[256];
        for (int i = 0; i < attachment.Length; i++) attachment[i] = (byte)(i & 0xFF);
        var ops = Enumerable.Range(0, 5).Select(i => new EditOp(i, $"o{i}")).ToArray();

        // Fire all four traffic shapes in flight at once.
        await using var pipe = alice.CreatePipe();
        await using var channel = bob.Nexus.Context.CreateChannel<EditOp>();

        var draftCall = alice.Server.SaveDraft("design.md", "draft-a").AsTask();
        var whisperCall = carol.Server.Whisper(bob.Nexus.Context.Id, "ping").AsTask();

        var uploadCall = alice.Server.UploadAttachment("design.md", pipe).AsTask();
        var uploadDrive = pipe.PipeUploadAsync(attachment).AsTask();

        var streamCall = bob.Server.StreamEdits("design.md", channel).AsTask();
        var streamDrive = Task.Run(async () =>
        {
            var writer = await channel.GetWriterAsync();
            foreach (var op in ops) await writer.WriteAsync(op);
            await writer.CompleteAsync();
        });

        await Task.WhenAll(draftCall, whisperCall, uploadDrive, uploadCall, streamDrive, streamCall)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Every callback delivered exactly once on the expected recipients; non-recipients
        // never see the per-target whisper.
        alice.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "draft-a"), times: 1);
        bob.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "draft-a"), times: 1);
        carol.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", "draft-a"), times: 1);
        bob.AssertReceived<IEditorClientNexus>(n => n.WhisperReceived("carol", "ping"), times: 1);
        alice.AssertNotReceived<IEditorClientNexus>(
            n => n.WhisperReceived(Arg.Any<string>(), Arg.Any<string>()));
        carol.AssertNotReceived<IEditorClientNexus>(
            n => n.WhisperReceived(Arg.Any<string>(), Arg.Any<string>()));
        Assert.That(EditorServerNexus.Documents["design.md"].Attachment, Is.EqualTo(attachment));
        Assert.That(EditorServerNexus.Documents["design.md"].Edits.ToArray(), Is.EqualTo(ops));
    }

    [Test]
    public async Task OpenDocument_NotifiesOthersExceptCaller()
    {
        // Pairs with LeaveDocument_NotifiesOthersExceptCaller — exercises the EditorJoined
        // GroupExceptCaller broadcast that fires inside OpenDocument.
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob"));
        var carol = await host.ConnectAsAsync(TestIdentity.Of("carol"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        // Carol joins last → alice + bob receive EditorJoined("carol"); carol does not see
        // her own join. Alice never sees her own join either (first joiner, group empty at
        // broadcast time).
        await carol.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        alice.AssertReceived<IEditorClientNexus>(n => n.EditorJoined("carol"), times: 1);
        bob.AssertReceived<IEditorClientNexus>(n => n.EditorJoined("carol"), times: 1);
        carol.AssertNotReceived<IEditorClientNexus>(n => n.EditorJoined("carol"));
        alice.AssertNotReceived<IEditorClientNexus>(n => n.EditorJoined("alice"));
    }

    [Test]
    public async Task Connect_WithoutIdentity_IsRejectedAtHandshake()
    {
        // The anonymous-call-to-gated-method path can't be exercised directly because the
        // framework rejects null-identity sessions at the handshake (NexusSession.Receiving
        // returns DisconnectReason.Authentication when Authenticate returns null). The
        // TestAuthenticationStore-backed override returns null for a missing token, so
        // ConnectAsync() (no identity) never completes a usable session and the OnAuthorize
        // override's `is not TestIdentity` guard isn't reachable via this harness's auth path.
        await using var host = await CreateHost();

        Assert.That(
            async () => await host.ConnectAsync(),
            Throws.InstanceOf<NexNet.Transports.TransportException>()
                .With.Message.Contains("Authentication"));
    }

    [Test]
    public async Task Groups_Introspection_ReflectsLiveMembership()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob"));
        var carol = await host.ConnectAsAsync(TestIdentity.Of("carol"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await carol.Server.OpenDocument("recipe.txt");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(host.Groups["doc-design.md"].Count, Is.EqualTo(2));
        Assert.That(host.Groups["doc-recipe.txt"].Count, Is.EqualTo(1));

        await bob.Server.LeaveDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(host.Groups["doc-design.md"].Count, Is.EqualTo(1));

        await carol.Server.OpenDocument("design.md");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(host.Groups["doc-design.md"].Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ServerSide_AssertReceived_SaveDraft()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));

        await alice.Server.OpenDocument("design.md");
        await alice.Server.SaveDraft("design.md", "v1");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        host.AssertReceived<IEditorServerNexus>(n => n.SaveDraft("design.md", "v1"));
        host.AssertReceived<IEditorServerNexus>(n => n.OpenDocument("design.md"));
    }

    [Test]
    public async Task AssertReceived_TimesMismatch_DiagnosticIncludesArgs()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));

        await alice.Server.OpenDocument("design.md");
        await alice.Server.SaveDraft("design.md", "v1");
        await alice.Server.SaveDraft("design.md", "v2");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        var ex = Assert.Throws<NexusAssertionException>(
            () => host.AssertReceived<IEditorServerNexus>(n => n.SaveDraft("design.md", "v3")));

        Assert.That(ex!.Message, Does.Contain("v1"));
        Assert.That(ex.Message, Does.Contain("v2"));
        Assert.That(ex.Message, Does.Contain("SaveDraft"));
    }

    [Test]
    public async Task ArgMatchers_AnyAndPredicate()
    {
        await using var host = await CreateHost();
        var alice = await host.ConnectAsAsync(TestIdentity.Of("alice", "Write"));
        var bob = await host.ConnectAsAsync(TestIdentity.Of("bob", "Write"));

        await alice.Server.OpenDocument("design.md");
        await bob.Server.OpenDocument("design.md");
        await alice.Server.SaveDraft("design.md", "v2-final");
        await host.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));

        bob.AssertReceived<IEditorClientNexus>(n => n.DraftSaved("alice", Arg.Any<string>()));
        bob.AssertReceived<IEditorClientNexus>(n => n.DraftSaved(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.StartsWith("v"))));
    }
}
