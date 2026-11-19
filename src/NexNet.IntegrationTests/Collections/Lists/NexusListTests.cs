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

/// <summary>Operations on a bidirectional list from the client under test and from the server.</summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusListTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
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
