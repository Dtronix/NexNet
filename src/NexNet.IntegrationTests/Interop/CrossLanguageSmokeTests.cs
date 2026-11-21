using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.InteropServer;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.RateLimiting;
using NexNet.Serialization;
using NexNet.TestNexus.Interop;
using NexNet.Transports;

// The C# test scaffolding (remote proxy) of version 1 of the versioned interop nexus, for the version smoke tests.
[assembly: NexNet.Interop.NexusTestScaffolding<NexNet.TestNexus.Interop.IInteropVersionedServerNexusV1, NexNet.TestNexus.Interop.IInteropVersionedClientNexus>]

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Interop;

/// <summary>
/// The cross-language smoke subset of bucket B (P01h): server-focused behavior where a foreign client's wire behavior
/// matters, on the client matrix (<see cref="CrossLanguageSmokeAttribute"/>). Every test uses the interop nexus
/// (<see cref="InteropServerNexus"/>), which Java and JavaScript export, on an <see cref="InteropServerHost"/> server
/// with every endpoint at once. The C# originals stay C#-only; each test names the one it mirrors.
/// </summary>
/// <remarks>
/// Clients authenticate with their name as the greeting token, so the server side finds each client's session
/// (<see cref="SmokeServer.SessionAsync"/>) to group it, address it or inject frames into it.
/// </remarks>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class CrossLanguageSmokeTests(LanguageHostPool hosts) : BaseTests
{
    /// <summary>The language hosts of the session; initializing them registers their drivers with <see cref="Harness"/>.</summary>
    protected LanguageHostPool Hosts { get; } = hosts;

    // ----- Broadcasts (NexusServerTests_NexusInvocations, NexusServerTests_NexusGroupInvocations) -----

    /// <summary>Mirrors <c>NexusServerTests_NexusInvocations.NexusInvokesOnAll</c>.</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Broadcast_All_ReachesEveryClient(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var a = await server.ConnectAsync(target, "a");
        await using var b = await server.ConnectAsync(target, "b");
        await server.SessionAsync("a");
        await server.SessionAsync("b");

        using (var owner = server.Server.Server.ContextProvider.Rent())
            owner.Proxy.All.ClientNotify("all");

        await AssertNextNotifyAsync(a, "all");
        await AssertNextNotifyAsync(b, "all");
        await EndAsync(a, b);
    }

    /// <summary>Mirrors <c>NexusServerTests_NexusGroupInvocations.NexusInvokesOnGroup</c>.</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Broadcast_Group_ReachesOnlyMembers(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var member = await server.ConnectAsync(target, "member");
        await using var other = await server.ConnectAsync(target, "other");
        await (await server.SessionAsync("member")).Context.Groups.AddAsync("g");
        await server.SessionAsync("other");

        using (var owner = server.Server.Server.ContextProvider.Rent())
            owner.Proxy.Group("g").ClientNotify("group");
        await AssertNextNotifyAsync(member, "group");

        // The non-member's first call is the later broadcast to all, not the group message.
        await server.NotifyAllAsync("sentinel");
        await AssertNextNotifyAsync(member, "sentinel");
        await AssertNextNotifyAsync(other, "sentinel");
        await EndAsync(member, other);
    }

    /// <summary>
    /// Mirrors <c>NexusServerTests_NexusGroupInvocations.NexusInvokesOnGroups</c>: the message goes to each group in
    /// turn, so a member of both groups receives it twice (current behavior, as the C# test counts it).
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Broadcast_Groups_ReachesMembersOfEachGroup(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var first = await server.ConnectAsync(target, "first");
        await using var both = await server.ConnectAsync(target, "both");
        await using var none = await server.ConnectAsync(target, "none");
        await (await server.SessionAsync("first")).Context.Groups.AddAsync("g1");
        await (await server.SessionAsync("both")).Context.Groups.AddAsync(["g1", "g2"]);
        await server.SessionAsync("none");

        using (var owner = server.Server.Server.ContextProvider.Rent())
            owner.Proxy.Groups(["g1", "g2"]).ClientNotify("groups");
        await AssertNextNotifyAsync(first, "groups");
        await AssertNextNotifyAsync(both, "groups");
        await AssertNextNotifyAsync(both, "groups");

        // The non-member receives none.
        await server.NotifyAllAsync("sentinel");
        await AssertNextNotifyAsync(first, "sentinel");
        await AssertNextNotifyAsync(both, "sentinel");
        await AssertNextNotifyAsync(none, "sentinel");
        await EndAsync(first, both, none);
    }

    /// <summary>A session removed from a group stops receiving its broadcasts (<c>GroupManager.RemoveAsync</c>).</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Broadcast_Group_RemovedMemberNoLongerReceives(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var stays = await server.ConnectAsync(target, "stays");
        await using var leaves = await server.ConnectAsync(target, "leaves");
        await (await server.SessionAsync("stays")).Context.Groups.AddAsync("g");
        var leavesSession = await server.SessionAsync("leaves");
        await leavesSession.Context.Groups.AddAsync("g");

        using (var owner = server.Server.Server.ContextProvider.Rent())
            owner.Proxy.Group("g").ClientNotify("before");
        await AssertNextNotifyAsync(stays, "before");
        await AssertNextNotifyAsync(leaves, "before");

        await leavesSession.Context.Groups.RemoveAsync("g");
        using (var owner = server.Server.Server.ContextProvider.Rent())
            owner.Proxy.Group("g").ClientNotify("after");
        await AssertNextNotifyAsync(stays, "after");

        await server.NotifyAllAsync("sentinel");
        await AssertNextNotifyAsync(stays, "sentinel");
        await AssertNextNotifyAsync(leaves, "sentinel");
        await EndAsync(stays, leaves);
    }

    /// <summary>Mirrors <c>NexusServerTests_NexusInvocations.NexusInvokesOnClient</c>.</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Broadcast_Client_ReachesOnlyThatClient(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var addressed = await server.ConnectAsync(target, "addressed");
        await using var other = await server.ConnectAsync(target, "other");
        var id = (await server.SessionAsync("addressed")).Context.Id;
        await server.SessionAsync("other");

        using (var owner = server.Server.Server.ContextProvider.Rent())
            owner.Proxy.Client(id).ClientNotify("direct");
        await AssertNextNotifyAsync(addressed, "direct");

        await server.NotifyAllAsync("sentinel");
        await AssertNextNotifyAsync(addressed, "sentinel");
        await AssertNextNotifyAsync(other, "sentinel");
        await EndAsync(addressed, other);
    }

    /// <summary>Mirrors <c>NexusServerTests_NexusInvocations.NexusInvokesOnClients</c>.</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Broadcast_Clients_ReachesOnlyThoseClients(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var a = await server.ConnectAsync(target, "a");
        await using var b = await server.ConnectAsync(target, "b");
        await using var c = await server.ConnectAsync(target, "c");
        var ids = new[] { (await server.SessionAsync("a")).Context.Id, (await server.SessionAsync("b")).Context.Id };
        await server.SessionAsync("c");

        using (var owner = server.Server.Server.ContextProvider.Rent())
            owner.Proxy.Clients(ids).ClientNotify("some");
        await AssertNextNotifyAsync(a, "some");
        await AssertNextNotifyAsync(b, "some");

        await server.NotifyAllAsync("sentinel");
        await AssertNextNotifyAsync(a, "sentinel");
        await AssertNextNotifyAsync(b, "sentinel");
        await AssertNextNotifyAsync(c, "sentinel");
        await EndAsync(a, b, c);
    }

    /// <summary>Mirrors <c>NexusServerTests_NexusInvocations.InvokesViaNexusContextAndGetsReturnFromSingleClient</c>.</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Broadcast_Client_ReturnsTheClientResult(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var client = await server.ConnectAsync(target, "client");
        client.Callbacks<InteropClientNexusScript>().ClientEchoString.Returns("from client");
        var id = (await server.SessionAsync("client")).Context.Id;

        string? result;
        using (var owner = server.Server.Server.ContextProvider.Rent())
            result = await owner.Proxy.Client(id).ClientEchoString("x").AsTask().Timeout(T(2));

        await Assert.That(result).IsEqualTo("from client");
        await Assert.That((await NextCallAsync(client.Callbacks<InteropClientNexusScript>().ClientEchoString)).value).IsEqualTo("x");
        await EndAsync(client);
    }

    // ----- Multi-endpoint server (NexusServerTests_MultiEndpoint) -----

    /// <summary>
    /// One server, clients of several languages on several endpoints: a broadcast to all reaches each. Mirrors the
    /// multi-endpoint broadcast reach of <c>NexusServerTests_MultiEndpoint</c> (S02).
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task MultiEndpoint_BroadcastReachesMixedClientsOnEveryEndpoint(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        var otherTransport = target.Transport == ClientTransport.WebSocketTls ? ClientTransport.HttpSocketTls : ClientTransport.WebSocketTls;

        // The language under test on the row's endpoint and, when it supports it, on the other secure HTTP endpoint;
        // a C# client on plain TCP.
        var sameLanguageOther = new ClientMatrixAttribute { Languages = [target.Language], Transports = [otherTransport] }.GetTargets()[0];
        var otherTarget = sameLanguageOther.SkipReason == null ? sameLanguageOther : ClientTarget.CSharp(otherTransport);

        await using var row = await server.ConnectAsync(target, "row");
        await using var other = await server.ConnectAsync(otherTarget, "other");
        await using var csharp = await server.ConnectAsync(ClientTarget.CSharp(ClientTransport.Tcp), "csharp");

        await Assert.That((await server.SessionAsync("row")).Context.Endpoint!.Name).IsEqualTo(target.Transport.ToEndpointKind().GetEndpointName());
        await Assert.That((await server.SessionAsync("other")).Context.Endpoint!.Name).IsEqualTo(otherTransport.ToEndpointKind().GetEndpointName());
        await Assert.That((await server.SessionAsync("csharp")).Context.Endpoint!.Name).IsEqualTo("tcp");

        await server.NotifyAllAsync("everyone");
        await AssertNextNotifyAsync(row, "everyone");
        await AssertNextNotifyAsync(other, "everyone");
        await AssertNextNotifyAsync(csharp, "everyone");
        await EndAsync(row, other, csharp);
    }

    /// <summary>
    /// The session sees the endpoint the client connected to (<c>Context.Endpoint</c>, S02). Mirrors
    /// <c>NexusServerTests_MultiEndpoint</c>'s endpoint info checks.
    /// </summary>
    [Test]
    [CrossLanguageSmoke(Transports = [ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.Uds, ClientTransport.HttpSocket, ClientTransport.HttpSocketTls, ClientTransport.WebSocket, ClientTransport.WebSocketTls])]
    public async Task MultiEndpoint_SessionSeesItsEndpoint(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var client = await server.ConnectAsync(target, "client");
        var kind = target.Transport.ToEndpointKind();

        var described = await client.Proxy<IInteropServerNexus>().GetEndpoint().AsTask().Timeout(T(2));
        var parts = described.Split('|');
        await Assert.That(parts[0]).IsEqualTo(kind.GetEndpointName());
        await Assert.That(parts[1]).IsEqualTo(kind.GetTransportName());
        await Assert.That(parts[2]).IsEqualTo(kind.IsSecure() ? "true" : "false");
        await EndAsync(client);
    }

    // ----- Rate limiting (ConnectionRateLimitingIntegrationTests) -----

    /// <summary>
    /// Over the server's connection limit the client is rejected cleanly: the connect fails and the client ends up
    /// disconnected, while the admitted client keeps working. Mirrors
    /// <c>ConnectionRateLimitingIntegrationTests.AspServer_RejectsConnectionsOverGlobalLimit</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    [NotInParallel("stress")]
    public async Task RateLimit_OverGlobalLimit_RejectedCleanly(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync(configure: OneConnection);
        await using var admitted = await server.ConnectAsync(target, "admitted");
        await server.SessionAsync("admitted");

        await using var rejected = await Harness.CreateClientAsync(target, server.Spec(target.Transport, "rejected"));
        await Assert.That(await rejected.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
        await Assert.That(await rejected.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);

        await Assert.That(await admitted.Proxy<IInteropServerNexus>().EchoInt32(7).AsTask().Timeout(T(2))).IsEqualTo(7);
        await EndAsync(admitted, rejected);
    }

    /// <summary>
    /// The limit's slot is released when the admitted client disconnects, so a new client connects. Mirrors
    /// <c>ConnectionRateLimitingIntegrationTests.AspServer_ReleasesSlotOnDisconnect</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    [NotInParallel("stress")]
    public async Task RateLimit_SlotReleasedOnDisconnect(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync(configure: OneConnection);
        await using (var first = await server.ConnectAsync(target, "first"))
        {
            await server.SessionAsync("first");
            await first.DisconnectAsync().Timeout(T(2));
            await EndAsync(first);
        }

        await WaitForAsync(() => server.Server.Server.GetSnapshot().RateLimiter?.CurrentConnections == 0, "the rate limiter slot released");

        await using var second = await server.ConnectAsync(target, "second");
        await Assert.That(await second.Proxy<IInteropServerNexus>().EchoInt32(8).AsTask().Timeout(T(2))).IsEqualTo(8);
        await EndAsync(second);
    }

    // ----- Version validation (NexusServerTests_Versioned) -----

    /// <summary>
    /// A client of version 1 connects to a server that accepts versions 1 and 2 and invokes it. Mirrors
    /// <c>NexusServerTests_Versioned.OldestServerProxy_InvokesCurrentAndOlderMethodVersions</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Versioned_OlderClientConnectsAndInvokes(ClientTarget target)
    {
        await using var server = await InteropServerHost.StartAsync<InteropVersionedServerNexus, InteropVersionedServerNexus.ClientProxy>(() => new());
        await using var client = await Harness.CreateClientAsync<InteropVersionedClientNexusV1, InteropVersionedClientNexusV1.ServerProxy>(
            target, ClientSpec.ForEndpoint(server.Endpoints, target.Transport));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        await Assert.That(await client.Proxy<IInteropVersionedServerNexusV1>().Add(2, 3).AsTask().Timeout(T(2))).IsEqualTo(5);
        await EndAsync(client);
    }

    /// <summary>
    /// A client of a version the server does not have is disconnected with <see cref="DisconnectReason.ServerMismatch"/>
    /// (spec §5.2, server check 8). Mirrors <c>NexusServerTests_Versioned.CurrentServerProxy_ConnectToOlderServerFails</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Versioned_NewerClientToOlderServer_ServerMismatch(ClientTarget target)
    {
        await using var server = await InteropServerHost.StartAsync<SmokeVersionOneServerNexus, SmokeVersionOneServerNexus.ClientProxy>(() => new());
        await using var client = await Harness.CreateClientAsync<InteropVersionedClientNexusV2, InteropVersionedClientNexusV2.ServerProxy>(
            target, ClientSpec.ForEndpoint(server.Endpoints, target.Transport));

        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
        await Assert.That(await client.WhenDisconnectedAsync(T(1))).IsEqualTo(DisconnectReason.ServerMismatch);
        await EndAsync(client);
    }

    /// <summary>
    /// A client of another client nexus is disconnected with <see cref="DisconnectReason.ClientMismatch"/> (spec §5.2,
    /// server check 5).
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Versioned_OtherClientNexus_ClientMismatch(ClientTarget target)
    {
        await using var server = await InteropServerHost.StartAsync<InteropVersionedServerNexus, InteropVersionedServerNexus.ClientProxy>(() => new());
        await using var client = await Harness.CreateClientAsync(target, ClientSpec.ForEndpoint(server.Endpoints, target.Transport));

        await Assert.That(await client.TryConnectAsync().Timeout(T(TestTimeouts.Connect))).IsFalse();
        await Assert.That(await client.WhenDisconnectedAsync(T(1))).IsEqualTo(DisconnectReason.ClientMismatch);
        await EndAsync(client);
    }

    // ----- Session invocation lifetime (NexusServerTests_InvocationLifetime) -----

    /// <summary>
    /// A hung invocation holds the session's only invocation slot, so the next call queues; canceling the hung call
    /// reaches it although the session is saturated, and the queued call then runs. Mirrors
    /// <c>NexusServerTests_InvocationLifetime.HungInvocationStarvesSessionInvocationSlots</c> and
    /// <c>SaturatedSessionStillProcessesCancellation</c>.
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Lifetime_HungInvocationHoldsTheSlot_CancellationStillReachesIt(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync(configure: (_, config) => config.MaxConcurrentConnectionInvocations = 1);
        await using var client = await server.ConnectAsync(target, "client");
        await server.SessionAsync("client");
        var proxy = client.Proxy<IInteropServerNexus>();

        using var cts = new CancellationTokenSource();
        var hung = proxy.Delay(600_000, 1, cts.Token).AsTask();
        await WaitForAsync(() => server.Invocations() is { Executing: 1 }, "the hung call executing");

        var queued = proxy.EchoInt32(2).AsTask();
        await WaitForAsync(() => server.Invocations() is { Queued: 1 }, "the second call queued");
        await Assert.That(queued.IsCompleted).IsFalse();

        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await hung.Timeout(T(2)));
        await Assert.That(await queued.Timeout(T(2))).IsEqualTo(2);
        await EndAsync(client);
    }

    /// <summary>
    /// The session ends when the client disconnects with an invocation running on the server: the client's pending
    /// call ends and the server no longer lists the session. Mirrors
    /// <c>NexusServerTests_InvocationLifetime.DisconnectDoesNotCancelRunningInvocation</c> from the client's side.
    /// </summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task Lifetime_DisconnectWithRunningInvocation_EndsTheSession(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var client = await server.ConnectAsync(target, "client");
        await server.SessionAsync("client");

        // The server method outlives the session (it is not canceled on disconnect); 30 s bounds the orphan.
        var running = client.Proxy<IInteropServerNexus>().Delay(30_000, 1, CancellationToken.None).AsTask();
        await WaitForAsync(() => server.Invocations() is { Executing: 1 }, "the call executing");

        await client.DisconnectAsync().Timeout(T(2));
        // Contract C-2: a call pending when the session ends fails as canceled.
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await running.Timeout(T(2)));
        await WaitForAsync(() => server.Server.Server.GetSnapshot().Sessions.Count == 0, "the session removed");
        await EndAsync(client);
    }

    // ----- Separate invocation pool (SeparatePoolTests) -----

    /// <summary>Mirrors <c>SeparatePoolTests.SeparatePoolCall_TakesNoNormalSlot_PlainCallRunsWhileItRuns</c>.</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task SeparatePool_CallTakesNoNormalSlot(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync(configure: (_, config) => config.MaxConcurrentConnectionInvocations = 1);
        await using var client = await server.ConnectAsync(target, "client");
        await server.SessionAsync("client");
        var proxy = client.Proxy<IInteropServerNexus>();

        var slow = proxy.SlowEcho(1, 3_000).AsTask();
        await WaitForAsync(() => server.Invocations() is { SeparatePoolExecuting: 1 }, "the separate-pool call executing");

        // The only normal slot is free: the plain call returns while the separate-pool call still runs.
        await Assert.That(await proxy.EchoInt32(2).AsTask().Timeout(T(1))).IsEqualTo(2);
        await Assert.That(slow.IsCompleted).IsFalse();
        await Assert.That(server.Invocations()!.Executing).IsEqualTo(0);

        await Assert.That(await slow.Timeout(T(5))).IsEqualTo(1);
        await EndAsync(client);
    }

    /// <summary>Mirrors <c>SeparatePoolTests.FullSeparatePool_QueuesSeparateCalls_NeverBlocksPlainCalls</c>.</summary>
    [Test]
    [CrossLanguageSmoke]
    public async Task SeparatePool_FullPoolQueuesSeparateCalls_NeverBlocksPlainCalls(ClientTarget target)
    {
        await using var server = await SmokeServer.StartAsync(configure: (_, config) =>
        {
            config.MaxConcurrentConnectionInvocations = 1;
            config.MaxSeparatePoolInvocations = 1;
        });
        await using var client = await server.ConnectAsync(target, "client");
        await server.SessionAsync("client");
        var proxy = client.Proxy<IInteropServerNexus>();

        var slow1 = proxy.SlowEcho(1, 2_000).AsTask();
        await WaitForAsync(() => server.Invocations() is { SeparatePoolExecuting: 1 }, "the first separate-pool call executing");
        var slow2 = proxy.SlowEcho(2, 100).AsTask();
        await WaitForAsync(() => server.Invocations() is { SeparatePoolQueued: 1 }, "the second separate-pool call queued");

        // Sent after the queued separate-pool call, the plain call completes first.
        await Assert.That(await proxy.EchoInt32(3).AsTask().Timeout(T(1))).IsEqualTo(3);
        await Assert.That(slow1.IsCompleted).IsFalse();
        await Assert.That(slow2.IsCompleted).IsFalse();

        // The queued call runs only after the first one finished.
        await Assert.That(await slow1.Timeout(T(5))).IsEqualTo(1);
        await Assert.That(await slow2.Timeout(T(2))).IsEqualTo(2);
        await EndAsync(client);
    }

    // ----- Client-side protocol hardening (H02 frame injection) -----

    /// <summary>An unknown message type disconnects the client with <see cref="DisconnectReason.ProtocolError"/> (spec §3).</summary>
    [Test]
    [CrossLanguageSmoke]
    public Task Hardening_UnknownMessageType_ProtocolError(ClientTarget target)
        => AssertInjectedFrameDisconnectsAsync(target, [0xC8]);

    /// <summary>A <c>ClientGreeting</c> sent to a client is a protocol error (spec §4.1).</summary>
    [Test]
    [CrossLanguageSmoke]
    public Task Hardening_ClientGreetingToClient_ProtocolError(ClientTarget target)
        => AssertInjectedFrameDisconnectsAsync(target, Frame(MessageType.ClientGreeting, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(1);
            w.Write(0);
        }));

    /// <summary>A second <c>ServerGreeting</c> is a protocol error (spec §5.2, client check 1).</summary>
    [Test]
    [CrossLanguageSmoke]
    public Task Hardening_SecondServerGreeting_ProtocolError(ClientTarget target)
        => AssertInjectedFrameDisconnectsAsync(target, Frame(MessageType.ServerGreeting, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(5);
            w.Write(0);
            w.Write(1L);
            w.Write(1024 * 1024);
            w.Write(16);
            w.Write(30_000u);
        }));

    /// <summary>An invocation of a method the client nexus does not have is a protocol error (spec §4.3).</summary>
    [Test]
    [CrossLanguageSmoke]
    public Task Hardening_InvocationOfUnknownMethod_ProtocolError(ClientTarget target)
        => AssertInjectedFrameDisconnectsAsync(target, Frame(MessageType.Invocation, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(4);
            w.Write((ushort)0);
            w.Write(ushort.MaxValue);
            w.Write((byte)1); // IgnoreReturn
            w.WriteArrayHeader(0);
        }));

    /// <summary>A result for an invocation the client is not waiting on is a protocol error (spec §6.4).</summary>
    [Test]
    [CrossLanguageSmoke]
    public Task Hardening_ResultForUnknownInvocation_ProtocolError(ClientTarget target)
        => AssertInjectedFrameDisconnectsAsync(target, Frame(MessageType.InvocationResult, (ref MsgPackWriter w) =>
        {
            w.WriteArrayHeader(2);
            w.Write((ushort)54_321);
            w.Write((byte)InvocationResultMessage.StateType.CompletedResult);
        }));

    /// <summary>A body that does not decode is a protocol error (spec §1).</summary>
    [Test]
    [CrossLanguageSmoke]
    public Task Hardening_UndecodableBody_ProtocolError(ClientTarget target)
        => AssertInjectedFrameDisconnectsAsync(target, RawFrame(MessageType.Invocation, [0xC1]));

    // ----- Helpers -----

    /// <summary>Allows one connection at a time on the whole server (the TCP endpoint holds server-wide settings).</summary>
    private static void OneConnection(InteropEndpointKind kind, ServerConfig config)
    {
        if (kind == InteropEndpointKind.Tcp)
            config.RateLimiting = new ConnectionRateLimitConfig { MaxConcurrentConnections = 1 };
    }

    /// <summary>
    /// Connects a client of <paramref name="target"/>, injects <paramref name="frame"/> into its session from the server
    /// and asserts the client disconnects with <see cref="DisconnectReason.ProtocolError"/>.
    /// </summary>
    private async Task AssertInjectedFrameDisconnectsAsync(ClientTarget target, byte[] frame)
    {
        await using var server = await SmokeServer.StartAsync();
        await using var client = await server.ConnectAsync(target, "client");
        var nexus = await server.SessionAsync("client");
        await Assert.That(await client.Proxy<IInteropServerNexus>().EchoInt32(1).AsTask().Timeout(T(2))).IsEqualTo(1);

        var session = (NexusSession<InteropServerNexus, InteropServerNexus.ClientProxy>)nexus.SessionContext.Session;
        await session.InternalSendRawAsync(frame).AsTask().Timeout(T(1));

        await Assert.That(await client.WhenDisconnectedAsync(T(2))).IsEqualTo(DisconnectReason.ProtocolError);
        await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
        await EndAsync(client);
    }

    /// <summary>Writes a MessagePack frame body.</summary>
    private delegate void BodyWriter(ref MsgPackWriter writer);

    /// <summary>A frame of <paramref name="type"/> whose MessagePack body <paramref name="body"/> writes.</summary>
    private static byte[] Frame(MessageType type, BodyWriter body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MsgPackWriter(buffer);
        body(ref writer);
        writer.Flush();
        return RawFrame(type, buffer.WrittenSpan);
    }

    /// <summary>A frame of <paramref name="type"/> with <paramref name="body"/> as it is.</summary>
    private static byte[] RawFrame(MessageType type, ReadOnlySpan<byte> body)
    {
        var frame = new byte[3 + body.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)body.Length);
        body.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>Asserts the next <c>ClientNotify</c> call <paramref name="client"/> received carries <paramref name="message"/>.</summary>
    private static async Task AssertNextNotifyAsync(IClientUnderTest client, string message)
    {
        var call = await NextCallAsync(client.Callbacks<InteropClientNexusScript>().ClientNotify);
        await Assert.That(call.message).IsEqualTo(message).Because(client.Target + " received another notification first.");
    }

    private static async Task<TArgs> NextCallAsync<TArgs, TResult>(ScriptSlot<TArgs, TResult> slot)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(T(2)));
        return await slot.NextCallAsync(timeout.Token);
    }

    /// <summary>Disconnects each client still connected, then asserts its leak audit is clean.</summary>
    private static async Task EndAsync(params IClientUnderTest[] clients)
    {
        foreach (var client in clients)
        {
            if (await client.GetStateAsync() != ConnectionState.Disconnected)
                await client.DisconnectAsync().Timeout(T(2));

            var report = await client.EndTestAuditAsync();
            await Assert.That(report.IsClean).IsTrue().Because(client.Target + ": " + report);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(T(2));
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Timed out waiting for " + what + ".");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// An interop server whose clients authenticate with their name, so a test finds each client's session.
    /// </summary>
    private sealed class SmokeServer : IAsyncDisposable
    {
        private readonly ConcurrentQueue<InteropServerNexus> _nexuses;

        private SmokeServer(InteropServer<InteropServerNexus, InteropServerNexus.ClientProxy> server, ConcurrentQueue<InteropServerNexus> nexuses)
        {
            Server = server;
            _nexuses = nexuses;
        }

        public InteropServer<InteropServerNexus, InteropServerNexus.ClientProxy> Server { get; }

        public static async Task<SmokeServer> StartAsync(Action<InteropEndpointKind, ServerConfig>? configure = null)
        {
            var nexuses = new ConcurrentQueue<InteropServerNexus>();
            var options = new InteropServerOptions
            {
                TokenValidator = (token, _) => ValueTask.FromResult<IIdentity?>(new DefaultIdentity
                {
                    DisplayName = token is { } value ? Encoding.UTF8.GetString(value.Span) : "",
                }),
                ConfigureEndpoint = configure,
            };
            var server = await InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() =>
            {
                var nexus = new InteropServerNexus();
                nexuses.Enqueue(nexus);
                return nexus;
            }, options);
            return new SmokeServer(server, nexuses);
        }

        /// <summary>The spec of a client named <paramref name="name"/> on <paramref name="transport"/>.</summary>
        public ClientSpec Spec(ClientTransport transport, string name)
        {
            var spec = ClientSpec.ForEndpoint(Server.Endpoints, transport);
            spec.AuthToken = name;
            return spec;
        }

        /// <summary>Creates and connects a client named <paramref name="name"/>; disposes it when the connect fails.</summary>
        public async Task<IClientUnderTest> ConnectAsync(ClientTarget target, string name)
        {
            var client = await Harness.CreateClientAsync(target, Spec(target.Transport, name));
            try
            {
                await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        /// <summary>The server nexus of the session of the client named <paramref name="name"/>.</summary>
        public async Task<InteropServerNexus> SessionAsync(string name)
        {
            InteropServerNexus? found = null;
            await WaitForAsync(() => (found = _nexuses.FirstOrDefault(n => IdentityOf(n) == name)) != null, "the session of " + name);
            return found!;
        }

        /// <summary>Calls <c>ClientNotify</c> on every client.</summary>
        public Task NotifyAllAsync(string message)
        {
            using var owner = Server.Server.ContextProvider.Rent();
            owner.Proxy.All.ClientNotify(message);
            return Task.CompletedTask;
        }

        /// <summary>The invocation snapshot of the server's only session.</summary>
        public NexusInvocationSnapshot? Invocations() => Server.Server.GetSnapshot().Sessions.SingleOrDefault()?.Invocations;

        private static string? IdentityOf(InteropServerNexus nexus)
        {
            try
            {
                return nexus.SessionContext is ServerSessionContext<InteropServerNexus.ClientProxy> context
                       && context.Session.State == ConnectionState.Connected
                    ? context.Identity?.DisplayName
                    : null;
            }
            catch (Exception)
            {
                // No session context yet.
                return null;
            }
        }

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }
}

/// <summary>A server of version 1 only of the versioned interop nexus, for <c>Versioned_NewerClientToOlderServer_ServerMismatch</c>.</summary>
[Nexus<IInteropVersionedServerNexusV1, IInteropVersionedClientNexus>(NexusType = NexusType.Server)]
internal partial class SmokeVersionOneServerNexus
{
    public ValueTask<int> Add(int a, int b) => new(a + b);

    public ValueTask<string> GetIdentity() => new(Context.Identity?.DisplayName ?? "");
}
