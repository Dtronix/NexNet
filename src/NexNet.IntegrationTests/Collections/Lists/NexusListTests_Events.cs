using NexNet.Collections;
using NexNet.Interop;
using NexNet.Interop.Hosts;

namespace NexNet.IntegrationTests.Collections.Lists;

/// <summary>
/// The change events a list raises on the server and on the client under test. The client's events are observed through
/// a list watch (<see cref="IRemoteList{T}.WatchAsync"/>), on every client language.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusListTests_Events(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerAddAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Add);
        await s.ServerList.AddAsync(77).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([77], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerAddAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.AddAsync(77).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Add);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([77], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerInsertAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Add);
        await s.ServerList.InsertAsync(0, 77).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([77], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerInsertAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.InsertAsync(0, 77).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Add);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([77], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerRemoveAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        await s.ServerList.AddAsync(77).Timeout(T(1));
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Remove);
        await s.ServerList.RemoveAsync(77).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerRemoveAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(77).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.RemoveAsync(77).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Remove);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerRemoveAtAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        await s.ServerList.AddAsync(77).Timeout(T(1));
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Remove);
        await s.ServerList.RemoveAtAsync(0).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerRemoveAtAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(77).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.RemoveAtAsync(0).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Remove);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerMoveAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Move);
        await s.ServerList.MoveAsync(0, 1).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([2, 1], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerMoveAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.MoveAsync(0, 1).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Move);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([2, 1], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerReplaceAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Replace);
        await s.ServerList.ReplaceAsync(0, 3).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([3, 2], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ServerReplaceAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.ReplaceAsync(0, 3).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Replace);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([3, 2], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientAddAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Add);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(77).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([77], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientAddAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.AddAsync(77).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Add);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([77], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientInsertAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Add);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.InsertAsync(0, 77).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([77], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientInsertAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.InsertAsync(0, 77).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Add);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([77], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientRemoveAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Remove);

        await s.ServerList.AddAsync(77).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.RemoveAsync(77).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientRemoveAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(77).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.RemoveAsync(77).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Remove);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientRemoveAtAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Remove);

        await s.ServerList.AddAsync(77).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.RemoveAtAsync(0).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientRemoveAtAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(77).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await s.ServerList.RemoveAtAsync(0).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Remove);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>(), CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientMoveAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Move);

        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.MoveAsync(0, 1).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([2, 1], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientMoveAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.MoveAsync(0, 1).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Move);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([2, 1], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientReplaceAsync_NotifiesServer(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        using var eventReg = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Replace);

        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.ReplaceAsync(0, 3).Timeout(T(1));

        await eventReg.Wait();
        await Assert.That(s.ServerList).IsEquivalentTo([3, 2], CollectionOrdering.Matching);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientReplaceAsync_NotifiesClient(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await s.ServerList.AddAsync(1).Timeout(T(1));
        await s.ServerList.AddAsync(2).Timeout(T(1));
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.ReplaceAsync(0, 3).Timeout(T(1));

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Replace);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo([3, 2], CollectionOrdering.Matching);
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientReceivesResetNoticeOnConnection(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Reset);
        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>());
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientReceivesResetNoticeOnDisconnection(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await list.DisableAsync().Timeout(T(1));
        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Reset, 2);

        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>());
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientReceivesResetNoticeOnReConnection(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await using var watch = await WatchAsync(list);

        // Each step is a pipe opened or closed in round trips. With 1 s bounds this test failed on QUIC in parallel
        // gates, where the load, not the code, sets how long a step takes. The re-enable must succeed (not be refused while
        // the first pipe is still being freed), or only two resets arrive and the wait below times out without saying why.
        await Assert.That(await list.EnableAsync().Timeout(T(TestTimeouts.CollectionEnable))).IsTrue();
        await list.DisableAsync().Timeout(T(TestTimeouts.CollectionEnable));
        await Assert.That(await list.EnableAsync().Timeout(T(TestTimeouts.CollectionEnable))).IsTrue()
            .Because("The re-enable was refused.");

        await WaitForChangesAsync(watch, NexusCollectionChangedAction.Reset, 3);

        await Assert.That(await ItemsAsync(list)).IsEquivalentTo(Array.Empty<int>());
        await watch.DisposeAsync();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task ClientDisconnectionTaskCompletesOnDisconnect(ClientTarget target)
    {
        RequireInProc(target, CSharpListApi);
        await using var s = await ConnectAsync(target);
        var list = s.List;
        var clientList = InProc(s.Client).Client.Proxy.IntListBi;
        await Assert.That(await list.EnableAsync().Timeout(T(1))).IsTrue();
        await Assert.That(clientList.DisabledTask.IsCompleted).IsFalse();
        await list.DisableAsync().Timeout(T(1));
        await Assert.That(clientList.DisabledTask.IsCompleted).IsTrue();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task ServerDisabledTaskThrows(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => s.ServerList.DisabledTask);
        await s.EndAsync();
    }
}
