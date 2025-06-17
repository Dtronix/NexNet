using NexNet.Collections;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections.Lists;

internal class NexusListTests_Events : NexusCollectionBaseTests
{
    #region Server Change Notifications
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerAddAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, _, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);
        
        await serverNexus.IntListBi.AddAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerAddAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        await serverNexus.IntListBi.AddAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerInsertAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, _, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);
        
        await serverNexus.IntListBi.InsertAsync(0, 77).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerInsertAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        await serverNexus.IntListBi.InsertAsync(0, 77).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerRemoveAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, _, _) = await ConnectServerAndClient(type);
        
        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);
        await serverNexus.IntListBi.RemoveAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerRemoveAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await serverNexus.IntListBi.RemoveAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerRemoveAtAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, _, _) = await ConnectServerAndClient(type);
        
        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);
        await serverNexus.IntListBi.RemoveAtAsync(0).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerRemoveAtAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await serverNexus.IntListBi.RemoveAtAsync(0).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerMoveAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, _, _) = await ConnectServerAndClient(type);
        
        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Move);
        await serverNexus.IntListBi.MoveAsync(0,1).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([2, 1]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerMoveAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Move);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await serverNexus.IntListBi.MoveAsync(0,1).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([2, 1]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerReplaceAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, _, _) = await ConnectServerAndClient(type);
        
        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Replace);
        await serverNexus.IntListBi.ReplaceAsync(0,3).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([3, 2]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerReplaceAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Replace);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await serverNexus.IntListBi.ReplaceAsync(0,3).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([3, 2]));
    }
    
    #endregion
    
    #region Client Change Notifications

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientAddAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);
        
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.AddAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientAddAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        await client.Proxy.IntListBi.AddAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientInsertAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);
        
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.InsertAsync(0, 77).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientInsertAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.InsertAsync(0, 77).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([77]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientRemoveAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.RemoveAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientRemoveAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.RemoveAsync(77).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientRemoveAtAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.RemoveAtAsync(0).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientRemoveAtAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await serverNexus.IntListBi.AddAsync(77).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await serverNexus.IntListBi.RemoveAtAsync(0).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientMoveAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Move);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.MoveAsync(0,1).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([2, 1]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientMoveAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Move);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.MoveAsync(0,1).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([2, 1]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientReplaceAsync_NotifiesServer(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = serverNexus.IntListBi.WaitForEvent(NexusCollectionChangedAction.Replace);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.ReplaceAsync(0,3).Timeout(1);

        await eventReg.Wait();
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo([3, 2]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientReplaceAsync_NotifiesClient(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Replace);

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.ReplaceAsync(0,3).Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([3, 2]));
    }
    

    #endregion
    
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientReceivesResetNoticeOnConnection(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Reset);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await eventReg.Wait();
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientReceivesResetNoticeOnDisconnection(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Reset, 2);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.DisconnectAsync().Timeout(1);
        await eventReg.Wait();

        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientReceivesResetNoticeOnReConnection(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Reset, 3);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.DisconnectAsync().Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await eventReg.Wait();

        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientDisconnectionTaskCompletesOnDisconnect(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        Assert.That(client.Proxy.IntListBi.DisconnectedTask.IsCompleted, Is.False);
        await client.Proxy.IntListBi.DisconnectAsync().Timeout(1);
        Assert.That(client.Proxy.IntListBi.DisconnectedTask.IsCompleted, Is.True);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerDisconnectionTaskIsAlwaysCompleted(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        Assert.That(serverNexus.IntListBi.DisconnectedTask.IsCompleted, Is.True);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        Assert.That(serverNexus.IntListBi.DisconnectedTask.IsCompleted, Is.True);
        await client.Proxy.IntListBi.DisconnectAsync().Timeout(1);
        Assert.That(serverNexus.IntListBi.DisconnectedTask.IsCompleted, Is.True);
    }
}
