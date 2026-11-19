using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Interop.InProc;
using NexNet.InteropServer;
using NexNet.Pipes.Broadcast;

namespace NexNet.IntegrationTests.Collections.Lists;

/// <summary>
/// Base of the synchronized collection tests on the client matrix (P01f): the client under test is created through
/// <see cref="Harness"/> for each <see cref="ClientTarget"/> row, the server is the C# <see cref="ServerNexus"/> on a
/// dedicated interop server, and the client's list is read through <see cref="IRemoteList{T}"/> snapshots.
/// </summary>
/// <remarks>
/// <para>
/// The QUIC rows of the C# suite run on WebSocketTls: the interop server has no QUIC endpoint and no other client
/// language has QUIC (H16 restores them).
/// </para>
/// <para>
/// The harness has no list change events yet, so tests about the client's <c>Changed</c> events, and tests of C#-only
/// list members (<c>IsReadOnly</c>, <c>CopyTo</c>, <c>DisabledTask</c>), drive the C# client directly and skip other
/// languages visibly (<see cref="RequireInProc"/>).
/// </para>
/// </remarks>
internal abstract class CollectionMatrixTests(LanguageHostPool hosts) : BaseTests
{
    /// <summary>Skip reason of tests that observe the client's list change events.</summary>
    protected const string NoListEvents = "the harness has no list change events (list.changed) yet (follow-up card)";

    /// <summary>Skip reason of tests of members only the C# list has.</summary>
    protected const string CSharpListApi = "tests a member of the C# INexusList API";

    /// <summary>Skip reason of tests that expect an argument exception from a list operation.</summary>
    protected const string NoArgumentErrorKind = "argument errors of list operations have no ErrorKind (follow-up card)";

    /// <summary>The name of the bidirectional collection most tests use.</summary>
    protected const string IntListBi = nameof(IServerNexus.IntListBi);

    private readonly TaskCompletionSource<ServerNexus> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The language hosts of the session; initializing them registers their drivers with <see cref="Harness"/>.</summary>
    protected LanguageHostPool Hosts { get; } = hosts;

    /// <summary>
    /// Starts a dedicated interop server of <see cref="ServerNexus"/>, then creates and connects the client of
    /// <paramref name="target"/>.
    /// </summary>
    protected async Task<CollectionSession> ConnectAsync(ClientTarget target)
    {
        LoggerMode = BasePipeTests.LogMode.OnTestFail;
        var server = await InteropServerHost.StartAsync<ServerNexus, ServerNexus.ClientProxy>(() =>
        {
            var nexus = new ServerNexus();
            nexus.OnConnectedEvent = connected =>
            {
                _connected.TrySetResult(connected);
                return ValueTask.CompletedTask;
            };
            return nexus;
        }, new InteropServerOptions { Logger = Logger }).Timeout(T(TestTimeouts.Connect));

        IClientUnderTest? client = null;
        try
        {
            var spec = ClientSpec.ForEndpoint(server.Endpoints, target.Transport);
            spec.Logger = Logger;
            client = await Harness.CreateClientAsync<ClientNexus, ClientNexus.ServerProxy>(target, spec);
            await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
            var nexus = await _connected.Task.Timeout(T(1));
            return new CollectionSession(server, client, nexus);
        }
        catch
        {
            if (client != null)
                await client.DisposeAsync();
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Skips a row of another language: the test drives the C# client directly, through <see cref="InProc"/>, because
    /// the harness cannot yet express what it checks (<paramref name="reason"/>).
    /// </summary>
    protected static void RequireInProc(ClientTarget target, string reason)
    {
        if (target.Language != ClientLanguages.CSharp)
            Skip.Test("C# client only: " + reason);
    }

    /// <summary>The C# client of an in-process row (<see cref="RequireInProc"/>).</summary>
    protected static InProcClientDriver<ClientNexus, ClientNexus.ServerProxy> InProc(IClientUnderTest client)
        => (InProcClientDriver<ClientNexus, ClientNexus.ServerProxy>)client;

    /// <summary>The items the client's list currently holds.</summary>
    protected static async Task<int[]> ItemsAsync(IRemoteList<int> list)
        => (await list.SnapshotAsync().AsTask().Timeout(T(1))).ToArray();

    /// <summary>Waits until the client's list holds exactly <paramref name="expected"/>, in order, and returns it.</summary>
    protected static async Task<int[]> WaitForItemsAsync(IRemoteList<int> list, params int[] expected)
        => (await list.WaitForAsync(items => items.SequenceEqual(expected), TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)))).ToArray();

    /// <summary>Waits until the client's list holds <paramref name="count"/> items and returns them.</summary>
    protected static async Task<int[]> WaitForCountAsync(IRemoteList<int> list, int count)
        => (await list.WaitForAsync(items => items.Count == count, TimeSpan.FromSeconds(T(TestTimeouts.CollectionEvent)))).ToArray();

    /// <summary>
    /// Disables the server collection's acknowledgments, so a client operation stays pending after the server applied it.
    /// </summary>
    protected static void DoNotSendAck(INexusList<int> serverList)
        => ((INexusBroadcastServerTestModifier)serverList).DoNotSendAck = true;

    /// <summary>A client and its dedicated server.</summary>
    protected sealed class CollectionSession(
        InteropServer<ServerNexus, ServerNexus.ClientProxy> server,
        IClientUnderTest client,
        ServerNexus nexus) : IAsyncDisposable
    {
        /// <summary>The interop server.</summary>
        public InteropServer<ServerNexus, ServerNexus.ClientProxy> Server { get; } = server;

        /// <summary>The client under test.</summary>
        public IClientUnderTest Client { get; } = client;

        /// <summary>The server nexus of the client's session.</summary>
        public ServerNexus Nexus { get; } = nexus;

        /// <summary>The client's view of <c>IntListBi</c>.</summary>
        public IRemoteList<int> List => Client.List<int>(IntListBi);

        /// <summary>The server's <c>IntListBi</c>.</summary>
        public INexusList<int> ServerList => Nexus.IntListBi;

        /// <summary>Disconnects the client when it is still connected, then asserts its leak audit is clean.</summary>
        public async Task EndAsync()
        {
            if (await Client.GetStateAsync() != ConnectionState.Disconnected)
                await Client.DisconnectAsync().Timeout(T(1));

            var report = await Client.EndTestAuditAsync();
            await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Client.DisposeAsync();
            }
            finally
            {
                await Server.DisposeAsync();
            }
        }
    }
}

/// <summary>Operations on a bidirectional list from the client under test and from the server.</summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusListTests(LanguageHostPool hosts) : CollectionMatrixTests(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientCanAdd(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(1).Timeout(T(1));
        await list.AddAsync(2).Timeout(T(1));
        await list.AddAsync(3).Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo(s.ServerList);
            await Assert.That(s.ServerList).IsEquivalentTo([1, 2, 3]);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientCanInsert(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.InsertAsync(0, 2).Timeout(T(1));
        await list.InsertAsync(1, 3).Timeout(T(1));
        await list.InsertAsync(0, 1).Timeout(T(1));
        using (Assert.Multiple())
        {
            var cList = await ItemsAsync(list);
            var sList = s.ServerList.ToArray();
            await Assert.That(cList).IsEquivalentTo(sList);
            await Assert.That(cList).IsEquivalentTo([1, 2, 3]);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientCanRemove(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(0).Timeout(T(1));
        await list.AddAsync(111).Timeout(T(1));
        await list.AddAsync(2).Timeout(T(1));
        await list.RemoveAsync(111).Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([0, 2], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientCanRemoveAt(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(0).Timeout(T(1));
        await list.AddAsync(111).Timeout(T(1));
        await list.AddAsync(2).Timeout(T(1));
        await list.RemoveAtAsync(1).Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([0, 2], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientCanMove(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(0).Timeout(T(1));
        await list.AddAsync(1).Timeout(T(1));
        await list.AddAsync(2).Timeout(T(1));
        await list.MoveAsync(0, 2).Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([1, 2, 0], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientCanReplace(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(0).Timeout(T(1));
        await list.AddAsync(1).Timeout(T(1));
        await list.AddAsync(2).Timeout(T(1));
        await list.ReplaceAsync(0, 3).Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([3, 1, 2], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerCanAdd(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.AddAsync(3).Timeout(T(1));

        // The client applies the server's changes asynchronously.
        var items = await WaitForItemsAsync(list, 1, 2, 3);
        using (Assert.Multiple())
        {
            await Assert.That(items).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerCanInsert(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.InsertAsync(0, 2).Timeout(T(1));
        await s.ServerList.InsertAsync(1, 3).Timeout(T(1));
        await s.ServerList.InsertAsync(0, 1).Timeout(T(1));

        // The client applies the server's changes asynchronously.
        var items = await WaitForItemsAsync(list, 1, 2, 3);
        using (Assert.Multiple())
        {
            await Assert.That(items).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerCanRemove(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.AddAsync(0).Timeout(T(1));
        await s.ServerList.AddAsync(111).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.RemoveAsync(111).Timeout(T(1));

        // The client applies the server's changes asynchronously.
        var items = await WaitForItemsAsync(list, 0, 2);
        using (Assert.Multiple())
        {
            await Assert.That(items).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([0, 2], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerCanRemoveAt(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.AddAsync(0).Timeout(T(1));
        await s.ServerList.AddAsync(111).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.RemoveAtAsync(1).Timeout(T(1));

        // The client applies the server's changes asynchronously.
        var items = await WaitForItemsAsync(list, 0, 2);
        using (Assert.Multiple())
        {
            await Assert.That(items).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([0, 2], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerCanMove(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.AddAsync(0).Timeout(T(1));
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.MoveAsync(0, 2).Timeout(T(1));

        // The client applies the server's changes asynchronously.
        var items = await WaitForItemsAsync(list, 1, 2, 0);
        using (Assert.Multiple())
        {
            await Assert.That(items).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([1, 2, 0], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerCanReplace(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.AddAsync(0).Timeout(T(1));
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.ReplaceAsync(0, 3).Timeout(T(1));

        // The client applies the server's changes asynchronously.
        var items = await WaitForItemsAsync(list, 3, 1, 2);
        using (Assert.Multiple())
        {
            await Assert.That(items).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([3, 1, 2], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerSendsInitialValues(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.AddAsync(3).Timeout(T(1));

        // Enabling completes once the client has the server's snapshot.
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(s.ServerList).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerAddAsync_PropagatesToClientImmediately(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await s.ServerList.AddAsync(77).Timeout(T(1));

        await Assert.That(await WaitForItemsAsync(list, 77)).IsEquivalentTo([77]);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerRemoveAsync_PropagatesToClientImmediately(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        // seed, then remove on server
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.RemoveAsync(1).Timeout(T(1));

        await Assert.That(await WaitForItemsAsync(list, 2)).IsEquivalentTo([2]);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ServerClearAsync_PropagatesToClientImmediately(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await s.ServerList.AddAsync(9).Timeout(T(1));
        await s.ServerList.AddAsync(8).Timeout(T(1));

        // Without the change events, the client must first have the items for the empty list to show the clear.
        await WaitForItemsAsync(list, 9, 8);
        await s.ServerList.ClearAsync().Timeout(T(1));

        await Assert.That(await WaitForItemsAsync(list)).IsEmpty();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task MixedOperations_MaintainConsistency(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        // client side
        await list.AddAsync(1).Timeout(T(1));
        await list.AddAsync(2).Timeout(T(1));

        // server side mutation
        await s.ServerList.InsertAsync(1, 99).Timeout(T(1));
        await WaitForItemsAsync(list, 1, 99, 2);

        // client continues
        await list.RemoveAsync(2).Timeout(T(1));
        await list.InsertAsync(2, 3).Timeout(T(1));

        var expected = new[] { 1, 99, 3 };
        await Assert.That(await WaitForItemsAsync(list, expected)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(s.ServerList).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientClearAsync_ReturnsTrueAndClearsList(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(10).Timeout(T(1));
        await list.AddAsync(20).Timeout(T(1));

        var cleared = await list.ClearAsync().Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(cleared).IsTrue();
            await Assert.That(await ItemsAsync(list)).IsEmpty();
            await Assert.That(s.ServerList).IsEmpty();
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientClearAsync_ReturnsTrueWhenAlreadyEmpty(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        var first = await list.ClearAsync().Timeout(T(1));
        var second = await list.ClearAsync().Timeout(T(1));
        using (Assert.Multiple())
        {
            await Assert.That(first).IsTrue();
            await Assert.That(second).IsTrue();
            await Assert.That(await ItemsAsync(list)).IsEmpty();
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ContainsReflectsAddsAndRemoves(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(5).Timeout(T(1));
        await list.AddAsync(7).Timeout(T(1));
        var items = await ItemsAsync(list);
        await Assert.That(items.Contains(5)).IsTrue();
        await Assert.That(items.Contains(8)).IsFalse();

        await list.RemoveAsync(5).Timeout(T(1));
        await Assert.That((await ItemsAsync(list)).Contains(5)).IsFalse();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task IndexOf_ReturnsCorrectPositionOrMinusOne(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(11).Timeout(T(1));
        await list.AddAsync(22).Timeout(T(1));
        await list.AddAsync(33).Timeout(T(1));

        var items = await ItemsAsync(list);
        using (Assert.Multiple())
        {
            await Assert.That(Array.IndexOf(items, 11)).IsEqualTo(0);
            await Assert.That(Array.IndexOf(items, 33)).IsEqualTo(2);
            await Assert.That(Array.IndexOf(items, 44)).IsEqualTo(-1);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task IsReadOnly_IsFalseOnBidirectional(ClientTarget target)
    {
        RequireInProc(target, CSharpListApi);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await Assert.That(InProc(s.Client).Client.Proxy.IntListBi.IsReadOnly).IsFalse();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task IsReadOnly_IsFalseOnServerToClient(ClientTarget target)
    {
        RequireInProc(target, CSharpListApi);
        await using var s = await ConnectAsync(target);
        var relay = InProc(s.Client).Client.Proxy.IntListRelay;
        await relay.EnableAsync().Timeout(T(1));

        await Assert.That(relay.IsReadOnly).IsFalse();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task Count_UpdatesAfterEachOperation(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await Assert.That((await ItemsAsync(list)).Length).IsEqualTo(0);

        await list.AddAsync(1).Timeout(T(1));
        await Assert.That((await ItemsAsync(list)).Length).IsEqualTo(1);

        await list.AddAsync(2).Timeout(T(1));
        await Assert.That((await ItemsAsync(list)).Length).IsEqualTo(2);

        await list.RemoveAtAsync(0).Timeout(T(1));
        await Assert.That((await ItemsAsync(list)).Length).IsEqualTo(1);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task Indexer_ReturnsCorrectElement(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(100).Timeout(T(1));
        await list.AddAsync(200).Timeout(T(1));

        var items = await ItemsAsync(list);
        await Assert.That(items[0]).IsEqualTo(100);
        await Assert.That(items[1]).IsEqualTo(200);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task InsertAsync_AtEndIndex_ReturnsTrue(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(1).Timeout(T(1));
        var result = await list.InsertAsync(1, 2).Timeout(T(1));

        using (Assert.Multiple())
        {
            await Assert.That(result).IsTrue();
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task StopsAsyncAwaitProcessOnDisconnect(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        DoNotSendAck(s.ServerList);

        // The server applies the add and holds back the acknowledgment; disconnecting the session must end the wait.
        using var serverAdded = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Add);
        var adding = list.AddAsync(1).AsTask();
        await serverAdded.Wait();
        _ = s.Nexus.SessionContext.DisconnectAsync();

        await adding.Timeout(T(1));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task StopsAsyncAwaitProcessOnClientSideDisconnect(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        DoNotSendAck(s.ServerList);

        // The server applies the add and holds back the acknowledgment; disabling the client's list must end the wait.
        using var serverAdded = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Add);
        var adding = list.AddAsync(1).AsTask();
        await serverAdded.Wait();
        var disabling = list.DisableAsync().AsTask();

        await adding.Timeout(T(1));
        await disabling.Timeout(T(1));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task RemoveAsync_NonExistent_ReturnsFalse(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(7).Timeout(T(1));
        var removed = await list.RemoveAsync(99).Timeout(T(1));

        using (Assert.Multiple())
        {
            await Assert.That(removed).IsFalse();
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo([7]);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task AddAsync_AllowsDuplicatesAndReturnsTrue(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        var first = await list.AddAsync(42).Timeout(T(1));
        var second = await list.AddAsync(42).Timeout(T(1));

        using (Assert.Multiple())
        {
            await Assert.That(first).IsTrue();
            await Assert.That(second).IsTrue();
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo([42, 42]);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task InsertAsync_OnEmptyAtZero_ReturnsTrue(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        var result = await list.InsertAsync(0, 99).Timeout(T(1));

        using (Assert.Multiple())
        {
            await Assert.That(result).IsTrue();
            await Assert.That(await ItemsAsync(list)).IsEquivalentTo([99]);
            await Assert.That(s.ServerList).IsEquivalentTo([99]);
        }

        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task CopyTo_CopiesAllElements(ClientTarget target)
    {
        RequireInProc(target, CSharpListApi);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(3).Timeout(T(1));
        await list.AddAsync(6).Timeout(T(1));
        await list.AddAsync(9).Timeout(T(1));

        var array = new int[3];
        InProc(s.Client).Client.Proxy.IntListBi.CopyTo(array, 0);

        await Assert.That(array).IsEquivalentTo([3, 6, 9]);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task CopyTo_NullArray_ThrowsArgumentNullException(ClientTarget target)
    {
        RequireInProc(target, CSharpListApi);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            InProc(s.Client).Client.Proxy.IntListBi.CopyTo(null!, 0));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task CopyTo_SmallArray_ThrowsArgumentException(ClientTarget target)
    {
        RequireInProc(target, CSharpListApi);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(1).Timeout(T(1));
        await list.AddAsync(2).Timeout(T(1));

        var tooSmall = new int[1];
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            InProc(s.Client).Client.Proxy.IntListBi.CopyTo(tooSmall, 0));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task InsertAsync_NegativeIndex_Throws(ClientTarget target)
    {
        RequireInProc(target, NoArgumentErrorKind);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => list.InsertAsync(-1, 0).Timeout(T(1)));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task Enumeration_YieldsElementsInOrder(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(5).Timeout(T(1));
        await list.AddAsync(10).Timeout(T(1));
        await list.AddAsync(15).Timeout(T(1));

        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(new[] { 5, 10, 15 }, CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task ClientDisconnectsAndClearsLocalList(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(5).Timeout(T(1));
        await list.AddAsync(10).Timeout(T(1));
        await list.AddAsync(15).Timeout(T(1));
        await list.DisableAsync().Timeout(T(1));

        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>());
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task ClientDisconnectsAndReconnects(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await list.AddAsync(5).Timeout(T(1));
        await list.AddAsync(10).Timeout(T(1));
        await list.AddAsync(15).Timeout(T(1));
        await list.DisableAsync().Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([5, 10, 15], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task RemoveAttAsync_NegativeIndex_Throws(ClientTarget target)
    {
        RequireInProc(target, NoArgumentErrorKind);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => list.RemoveAtAsync(-1).Timeout(T(1)));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls])]
    public async Task ClientEnableAfterServerAdds_NoDuplicateItems(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;

        // Add items on server BEFORE client enables collection sync.
        // This creates a window where broadcast messages for these adds
        // could overlap with the reset snapshot sent during EnableAsync.
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await s.ServerList.AddAsync(3).Timeout(T(1));

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        // Perform a move to verify the client state has no duplicates.
        await list.MoveAsync(0, 2).Timeout(T(1));

        var items = await ItemsAsync(list);
        using (Assert.Multiple())
        {
            await Assert.That(items.Length).IsEqualTo(3);
            await Assert.That(items).IsEquivalentTo(s.ServerList, CollectionOrdering.Matching);
            await Assert.That(items).IsEquivalentTo([2, 3, 1], CollectionOrdering.Matching);
        }

        await s.EndAsync();
    }
}
