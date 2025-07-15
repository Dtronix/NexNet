using NexNet.Collections;
using NexNet.IntegrationTests.Pipes;
using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections.Lists;

internal class NexusListTests : NexusCollectionBaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanAdd(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(2).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(3).Timeout(1);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanInsert(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.InsertAsync(0, 2);
        await client.Proxy.IntListBi.InsertAsync(1, 3);
        await client.Proxy.IntListBi.InsertAsync(0, 1);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanRemove(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.AddAsync(0);
        await client.Proxy.IntListBi.AddAsync(111);
        await client.Proxy.IntListBi.AddAsync(2);
        await client.Proxy.IntListBi.RemoveAsync(111);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([0, 2]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanRemoveAt(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.AddAsync(0);
        await client.Proxy.IntListBi.AddAsync(111);
        await client.Proxy.IntListBi.AddAsync(2);
        await client.Proxy.IntListBi.RemoveAtAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([0, 2]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanMove(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.AddAsync(0);
        await client.Proxy.IntListBi.AddAsync(1);
        await client.Proxy.IntListBi.AddAsync(2);
        await client.Proxy.IntListBi.MoveAsync(0,2);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1,2,0]));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientCanReplace(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        await client.Proxy.IntListBi.AddAsync(0);
        await client.Proxy.IntListBi.AddAsync(1);
        await client.Proxy.IntListBi.AddAsync(2);
        await client.Proxy.IntListBi.ReplaceAsync(0,3);
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([3,1,2]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerCanAdd(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add, 3);
        
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await serverNexus.IntListBi.AddAsync(3).Timeout(1);

        await eventReg.Wait();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerCanInsert(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add, 3);

        await client.Proxy.IntListBi.ConnectAsync();
        await serverNexus.IntListBi.InsertAsync(0, 2);
        await serverNexus.IntListBi.InsertAsync(1, 3);
        await serverNexus.IntListBi.InsertAsync(0, 1);
        
        await eventReg.Wait();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerCanRemove(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await client.Proxy.IntListBi.ConnectAsync();
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await serverNexus.IntListBi.AddAsync(0);
        await serverNexus.IntListBi.AddAsync(111);
        await serverNexus.IntListBi.AddAsync(2);
        await serverNexus.IntListBi.RemoveAsync(111);

        await eventReg.Wait();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([0, 2]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerCanRemoveAt(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Remove);

        await client.Proxy.IntListBi.ConnectAsync();
        await serverNexus.IntListBi.AddAsync(0);
        await serverNexus.IntListBi.AddAsync(111);
        await serverNexus.IntListBi.AddAsync(2);
        await serverNexus.IntListBi.RemoveAtAsync(1);

        await eventReg.Wait();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([0, 2]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerCanMove(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Move);

        await client.Proxy.IntListBi.ConnectAsync();
        await serverNexus.IntListBi.AddAsync(0);
        await serverNexus.IntListBi.AddAsync(1);
        await serverNexus.IntListBi.AddAsync(2);
        await serverNexus.IntListBi.MoveAsync(0,2);

        await eventReg.Wait();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1,2,0]));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerCanReplace(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Replace);

        await client.Proxy.IntListBi.ConnectAsync();
        await serverNexus.IntListBi.AddAsync(0);
        await serverNexus.IntListBi.AddAsync(1);
        await serverNexus.IntListBi.AddAsync(2);
        await serverNexus.IntListBi.ReplaceAsync(0,3);

        await eventReg.Wait();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([3,1,2]));
        });
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerSendsInitialValues(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);

        await serverNexus.IntListBi.AddAsync(1);
        await serverNexus.IntListBi.AddAsync(2);
        await serverNexus.IntListBi.AddAsync(3);

        await client.Proxy.IntListBi.ConnectAsync();
        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(serverNexus.IntListBi));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([1, 2, 3]));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerAddAsync_PropagatesToClientImmediately(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        using var eventReg = client.Proxy.IntListBi.WaitForEvent(NexusCollectionChangedAction.Add);
        
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
    public async Task ServerRemoveAsync_PropagatesToClientImmediately(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        // seed, then remove on server
        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await serverNexus.IntListBi.RemoveAsync(1).Timeout(1);
        await Task.Delay(30);

        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([2]));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ServerClearAsync_PropagatesToClientImmediately(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await serverNexus.IntListBi.AddAsync(9).Timeout(1);
        await serverNexus.IntListBi.AddAsync(8).Timeout(1);
        await serverNexus.IntListBi.ClearAsync().Timeout(1);
        await Task.Delay(50);

        Assert.That(client.Proxy.IntListBi, Is.Empty);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task MixedOperations_MaintainConsistency(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        // client side
        await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(2).Timeout(1);

        // server side mutation
        await serverNexus.IntListBi.InsertAsync(1, 99).Timeout(1);
        await Task.Delay(50);

        // client continues
        await client.Proxy.IntListBi.RemoveAsync(2).Timeout(1);
        await client.Proxy.IntListBi.InsertAsync(2, 3).Timeout(1);

        var expected = new[] { 1, 99, 3 };
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(expected));
        Assert.That(serverNexus.IntListBi, Is.EquivalentTo(expected));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientClearAsync_ReturnsTrueAndClearsList(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        await client.Proxy.IntListBi.AddAsync(10).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(20).Timeout(1);

        var cleared = await client.Proxy.IntListBi.ClearAsync().Timeout(1);
        Assert.Multiple(() =>
        {
            Assert.That(cleared, Is.True);
            Assert.That(client.Proxy.IntListBi, Is.Empty);
            Assert.That(serverNexus.IntListBi, Is.Empty);
        });
    }
     
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientClearAsync_ReturnsTrueWhenAlreadyEmpty(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        var first = await client.Proxy.IntListBi.ClearAsync().Timeout(1);
        var second = await client.Proxy.IntListBi.ClearAsync().Timeout(1);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
            Assert.That(client.Proxy.IntListBi, Is.Empty);
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ContainsReflectsAddsAndRemoves(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(5).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(7).Timeout(1);
        Assert.That(client.Proxy.IntListBi.Contains(5), Is.True);
        Assert.That(client.Proxy.IntListBi.Contains(8), Is.False);

        await client.Proxy.IntListBi.RemoveAsync(5).Timeout(1);
        Assert.That(client.Proxy.IntListBi.Contains(5), Is.False);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task IndexOf_ReturnsCorrectPositionOrMinusOne(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(11).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(22).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(33).Timeout(1);

        Assert.Multiple(() =>
        {
            Assert.That(client.Proxy.IntListBi.IndexOf(11), Is.EqualTo(0));
            Assert.That(client.Proxy.IntListBi.IndexOf(33), Is.EqualTo(2));
            Assert.That(client.Proxy.IntListBi.IndexOf(44), Is.EqualTo(-1));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task IsReadOnly_IsFalseOnBidirectional(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        Assert.That(client.Proxy.IntListBi.IsReadOnly, Is.False);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task IsReadOnly_IsTrueOnServerToClient(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListSvToCl.ConnectAsync().Timeout(1);

        Assert.That(client.Proxy.IntListSvToCl.IsReadOnly, Is.True);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Count_UpdatesAfterEachOperation(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(0));

        await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(1));

        await client.Proxy.IntListBi.AddAsync(2).Timeout(1);
        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(2));

        await client.Proxy.IntListBi.RemoveAtAsync(0).Timeout(1);
        Assert.That(client.Proxy.IntListBi.Count, Is.EqualTo(1));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task Indexer_ReturnsCorrectElement(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(100).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(200).Timeout(1);

        Assert.That(client.Proxy.IntListBi[0], Is.EqualTo(100));
        Assert.That(client.Proxy.IntListBi[1], Is.EqualTo(200));
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task InsertAsync_AtEndIndex_ReturnsTrue(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
        var result = await client.Proxy.IntListBi.InsertAsync(1, 2).Timeout(1);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([1, 2]));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task StopsAsyncAwaitProcessOnDisconnect(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        var internalList = (NexusCollection)serverNexus.IntListBi;
        internalList.DoNotSendAck = true;

        await Utilities.InvokeAndNotifyAwait(async () =>
        {
            await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
            tcs.SetResult();
        }, () =>
        {
            serverNexus.SessionContext.DisconnectAsync();
        });

        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task StopsAsyncAwaitProcessOnClientSideDisconnect(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        var internalList = (NexusCollection)serverNexus.IntListBi;
        internalList.DoNotSendAck = true;

        await Utilities.InvokeAndNotifyAwait(async () =>
        {
            await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
            tcs.SetResult();
        }, () =>
        {
            client.Proxy.IntListBi.DisconnectAsync();
        });

        await tcs.Task.Timeout(1);
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task RemoveAsync_NonExistent_ReturnsFalse(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(7).Timeout(1);
        var removed = await client.Proxy.IntListBi.RemoveAsync(99).Timeout(1);

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.False);
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([7]));
        });
    }
    
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task AddAsync_AllowsDuplicatesAndReturnsTrue(Type type)
    {
        var (_, _, client, _) = await ConnectServerAndClient(type);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        var first = await client.Proxy.IntListBi.AddAsync(42).Timeout(1);
        var second = await client.Proxy.IntListBi.AddAsync(42).Timeout(1);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([42, 42]));
        });
    }
    
    [TestCase(Type.Uds)]
    public async Task InsertAsync_OnEmptyAtZero_ReturnsTrue(Type type)
    {
        var (_, serverNexus, client, _) = await ConnectServerAndClient(type);
        
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        var result = await client.Proxy.IntListBi.InsertAsync(0, 99).Timeout(1);
        
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.True);
            Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([99]));
            Assert.That(serverNexus.IntListBi, Is.EquivalentTo([99]));
        });
    }
    
    [Test]
    public async Task CopyTo_CopiesAllElements()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(3).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(6).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(9).Timeout(1);

        var array = new int[3];
        client.Proxy.IntListBi.CopyTo(array, 0);

        Assert.That(array, Is.EquivalentTo([3, 6, 9]));
    }
    
    [Test]
    public async Task CopyTo_NullArray_ThrowsArgumentNullException()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        Assert.Throws<ArgumentNullException>(() =>
            client.Proxy.IntListBi.CopyTo(null!, 0));
    }

    [Test]
    public async Task CopyTo_SmallArray_ThrowsArgumentException()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(1).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(2).Timeout(1);

        var tooSmall = new int[1];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            client.Proxy.IntListBi.CopyTo(tooSmall, 0));
    }
    
    [Test]
    public async Task InsertAsync_NegativeIndex_Throws()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Proxy.IntListBi.InsertAsync(-1, 0).Timeout(1));
    }
    
    [Test]
    public async Task Enumeration_YieldsElementsInOrder()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(5).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(10).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(15).Timeout(1);

        var list = client.Proxy.IntListBi.ToList();
        Assert.That(list, Is.EqualTo(new[] { 5, 10, 15 }));
    }
    
    [Test]
    public async Task ClientDisconnectsAndClearsLocalList()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(5).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(10).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(15).Timeout(1);
        await client.Proxy.IntListBi.DisconnectAsync().Timeout(1);
        
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo(Array.Empty<int>()));
    }
    
    [Test]
    public async Task ClientDisconnectsAndReconnects()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        await client.Proxy.IntListBi.AddAsync(5).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(10).Timeout(1);
        await client.Proxy.IntListBi.AddAsync(15).Timeout(1);
        await client.Proxy.IntListBi.DisconnectAsync().Timeout(1);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);
        
        Assert.That(client.Proxy.IntListBi, Is.EquivalentTo([5, 10, 15]));
    }
    
        
    [Test]
    public async Task RemoveAttAsync_NegativeIndex_Throws()
    {
        var (_, _, client, _) = await ConnectServerAndClient(Type.Uds);
        await client.Proxy.IntListBi.ConnectAsync().Timeout(1);

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Proxy.IntListBi.RemoveAtAsync(-1).Timeout(1));
    }
}
