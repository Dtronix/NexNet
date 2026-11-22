using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Interop;
using NexNet.Interop.Hosts;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// Noop answers, client protocol errors and the client collection state (protocol specification §12).
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionNoopTests(LanguageHostPool hosts) : NexusCollectionBaseTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are skipped.</summary>
    public LanguageHostPool Hosts { get; } = hosts;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ConcurrentRemoveOfSameIndex_LoserCompletesFalse(ClientTarget target)
    {
        const int items = 10;
        var (spec, nexuses) = await StartMatrixServerAsync(target);
        await using var client1 = await ConnectMatrixClientAsync(target, spec);
        await using var client2 = await ConnectMatrixClientAsync(target, spec);
        var serverList = nexuses.First().IntListBi;

        for (var i = 0; i < items; i++)
            await serverList.AddAsync(i).Timeout(T(1));

        var list1 = IntListBi(client1);
        var list2 = IntListBi(client2);
        await Assert.That(await list1.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await Assert.That(await list2.EnableAsync().AsTask().Timeout(T(1))).IsTrue();

        // Both requests must be built from the same base version. The C# client reads its version synchronously before
        // sending, so the other client's change cannot have been applied yet. A language host gets the two requests one
        // after the other, and the second client may already hold the first one's change, so both requests win: retry
        // until a round produces the Noop.
        var noop = false;
        for (var round = 0; round < items / 2 && !noop; round++)
        {
            var expected = serverList.ToArray();
            await WaitForItemsAsync(list1, expected, $"round {round}: list 1 before the removes");
            await WaitForItemsAsync(list2, expected, $"round {round}: list 2 before the removes");

            var results = await Task.WhenAll(list1.RemoveAtAsync(0).AsTask(), list2.RemoveAtAsync(0).AsTask()).Timeout(T(1));
            await Assert.That(results.Contains(true)).IsTrue().Because($"round {round}: one request wins");
            noop = results.Contains(false);

            // Whatever the outcome, both lists converge on the server's.
            expected = serverList.ToArray();
            await WaitForItemsAsync(list1, expected, $"round {round}: list 1 after the removes");
            await WaitForItemsAsync(list2, expected, $"round {round}: list 2 after the removes");
        }

        await Assert.That(noop).IsTrue().Because("one round must produce a Noop");

        // The collection still works after the Noop.
        await Assert.That(await list2.AddAsync(100).AsTask().Timeout(T(1))).IsTrue();
        await WaitForItemsAsync(list1, serverList.ToArray(), "list 1 after the add");

        await list1.DisableAsync().AsTask().Timeout(T(1));
        await list2.DisableAsync().AsTask().Timeout(T(1));
        await MatrixTestBase.EndAsync(client1);
        await MatrixTestBase.EndAsync(client2);
    }

    [Test]
    [Arguments(Type.Tcp, 5)]
    [Arguments(Type.Tcp, 1)]
    [Arguments(Type.Uds, 5)]
    [Arguments(Type.Uds, 1)]
    public async Task MalformedResetValueCount_ClosesPipe(Type type, int announcedValues)
    {
        var (server, client, _) = await ConnectServerAndClient(type);
        var serverNexus = server.NexusCreatedQueue.First();

        await serverNexus.IntListBi.AddAsync(1).Timeout(1);
        await serverNexus.IntListBi.AddAsync(2).Timeout(1);
        await serverNexus.IntListBi.AddAsync(3).Timeout(1);

        // The server announces a value count that does not match the values it sends.
        ((NexusListServer<int>)serverNexus.IntListBi).TestResetTotalValuesOverride = announcedValues;

        var list = client.Proxy.IntListBi;
        var enabled = await list.EnableAsync().Timeout(1);
        await list.DisabledTask.Timeout(1);

        using (Assert.Multiple())
        {
            await Assert.That(enabled).IsFalse();
            await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);
            await Assert.That(list.Count).IsEqualTo(0);

        }

        // The session stays up, and a correct reset succeeds.
        ((NexusListServer<int>)serverNexus.IntListBi).TestResetTotalValuesOverride = null;
        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(list).IsEquivalentTo(new[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task State_FollowsEnableAndDisable(ClientTarget target)
    {
        var (spec, _) = await StartMatrixServerAsync(target);
        await using var client = await ConnectMatrixClientAsync(target, spec);
        var list = IntListBi(client);

        await Assert.That(await StateAsync(list)).IsEqualTo(NexusCollectionState.Disconnected);

        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await Assert.That(await StateAsync(list)).IsEqualTo(NexusCollectionState.Connected);

        await list.DisableAsync().AsTask().Timeout(T(1));
        await Assert.That(await StateAsync(list)).IsEqualTo(NexusCollectionState.Disconnected);

        await Assert.That(await list.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await Assert.That(await StateAsync(list)).IsEqualTo(NexusCollectionState.Connected);

        // The session ends: the collection's disabled signal completes and it reports Disconnected.
        await client.DisconnectAsync().Timeout(T(1));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(T(1));
        var state = await list.GetStateAsync().AsTask().Timeout(T(1));
        while (!state.IsDisabled && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            state = await list.GetStateAsync().AsTask().Timeout(T(1));
        }

        using (Assert.Multiple())
        {
            await Assert.That(state.IsDisabled).IsTrue().Because("the disabled signal completes when the session ends");
            await Assert.That(state.State).IsEqualTo(NexusCollectionState.Disconnected);
        }

        await MatrixTestBase.EndAsync(client);
    }

    private static async Task<NexusCollectionState> StateAsync(IRemoteList<int> list)
        => (await list.GetStateAsync().AsTask().Timeout(T(1))).State;

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task EnableTokenCanceledAfterEnable_DisableStillCleansUp(Type type)
    {
        var (_, client, _) = await ConnectServerAndClient(type);
        var list = client.Proxy.IntListBi;

        using var cts = new CancellationTokenSource();
        await Assert.That(await list.EnableAsync(cts.Token).Timeout(1)).IsTrue();

        // Canceling the enable token must not stop the cleanup that runs when the pipe closes.
        await cts.CancelAsync();

        await list.DisableAsync().Timeout(1);
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Disconnected);

        await Assert.That(await list.EnableAsync().Timeout(1)).IsTrue();
        await Assert.That(list.State).IsEqualTo(NexusCollectionState.Connected);
    }
}