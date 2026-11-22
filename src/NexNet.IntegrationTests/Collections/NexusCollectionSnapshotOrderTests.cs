using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Interop.InProc;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// The server sends a new client its snapshot before any change, and answers a Noop after the change that made the
/// request obsolete (protocol specification §12).
/// </summary>
[NotInParallel("stress")]
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionSnapshotOrderTests(LanguageHostPool hosts) : NexusCollectionBaseTests
{
    /// <summary>The language hosts of the session; their hello decides which rows are skipped.</summary>
    public LanguageHostPool Hosts { get; } = hosts;

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ClientsJoiningMidStream_ReceiveSnapshotBeforeChanges(ClientTarget target)
    {
        const int writers = 4;
        const int writesPerWriter = 150;
        const int joiners = 6;

        var (spec, nexuses) = await StartMatrixServerAsync(target);
        var clients = new List<IClientUnderTest>();
        try
        {
            for (var i = 0; i < joiners + 1; i++)
                clients.Add(await ConnectMatrixClientAsync(target, spec));

            var serverList = nexuses.First().IntListBi;
            var writerList = IntListBi(clients[0]);
            await Assert.That(await writerList.EnableAsync().AsTask().Timeout(T(1))).IsTrue();

            // An async gate: blocking eleven pool threads on a wait handle starves the parallel tests next to this one.
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Server writers mix inserts, removes and replaces; one client writer goes through the processor too.
            var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                for (var i = 0; i < writesPerWriter; i++)
                {
                    var value = w * 100_000 + i;
                    switch (i % 5)
                    {
                        case 3 when serverList.Count > 10:
                            await serverList.RemoveAtAsync(0).ConfigureAwait(false);
                            break;
                        case 4 when serverList.Count > 0:
                            await serverList.ReplaceAsync(0, value).ConfigureAwait(false);
                            break;
                        default:
                            await serverList.AddAsync(value).ConfigureAwait(false);
                            break;
                    }
                }
            })).Append(Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                for (var i = 0; i < writesPerWriter; i++)
                    await writerList.AddAsync(-i - 1).ConfigureAwait(false);
            })).ToArray();

            // Joiners enable while the writers run, then disable and enable again to take a second snapshot.
            var joinerTasks = clients.Skip(1).Select((c, i) => Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                var list = IntListBi(c);
                await Task.Delay(i * 3).ConfigureAwait(false);
                if (!await list.EnableAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("Enable failed.");
                await Task.Delay(5).ConfigureAwait(false);
                await list.DisableAsync().ConfigureAwait(false);
                if (!await list.EnableAsync().ConfigureAwait(false))
                    throw new InvalidOperationException("Second enable failed.");
            })).ToArray();

            start.SetResult();
            await Task.WhenAll(writerTasks.Concat(joinerTasks)).Timeout(T(20));

            // Every broadcast is queued; wait for the clients to apply them. A client that applied a change before its
            // snapshot would end up with other contents.
            var expected = serverList.ToArray();
            for (var i = 0; i < clients.Count; i++)
                await WaitForItemsAsync(IntListBi(clients[i]), expected, $"list {i} contents");

            for (var i = 0; i < clients.Count; i++)
            {
                var state = await IntListBi(clients[i]).GetStateAsync().AsTask().Timeout(T(1));
                await Assert.That(state.State).IsEqualTo(NexusCollectionState.Connected).Because($"list {i} state");
            }

            // The C# client also counts the changes it received before its snapshot (a runtime test field; see
            // ClientParityExceptions.ListTestCounter). The other languages are checked by their contents above.
            if (target.Language == ClientLanguages.CSharp)
            {
                for (var i = 0; i < clients.Count; i++)
                {
                    var list = (NexusListClient<int>)((InProcClientDriver<ClientNexus, ClientNexus.ServerProxy>)clients[i]).Client.Proxy.IntListBi;
                    await Assert.That(list.TestChangesReceivedBeforeSnapshot).IsEqualTo(0)
                        .Because($"list {i} must not receive changes before its snapshot");
                }
            }

            foreach (var client in clients)
            {
                await IntListBi(client).DisableAsync().AsTask().Timeout(T(1));
                await MatrixTestBase.EndAsync(client);
            }
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task ConcurrentRemove_LoserSeesWinningChangeBeforeFalse(ClientTarget target)
    {
        const int rounds = 30;

        var (spec, nexuses) = await StartMatrixServerAsync(target);
        await using var client1 = await ConnectMatrixClientAsync(target, spec);
        await using var client2 = await ConnectMatrixClientAsync(target, spec);
        var serverList = nexuses.First().IntListBi;

        for (var i = 0; i < rounds; i++)
            await serverList.AddAsync(i).Timeout(T(1));

        var list1 = IntListBi(client1);
        var list2 = IntListBi(client2);
        await Assert.That(await list1.EnableAsync().AsTask().Timeout(T(1))).IsTrue();
        await Assert.That(await list2.EnableAsync().AsTask().Timeout(T(1))).IsTrue();

        var noops = 0;
        for (var round = 0; round < rounds / 2; round++)
        {
            // Both lists hold every change made so far: each request completes after its own change, or after the
            // change that made it obsolete.
            var expected = serverList.ToArray();
            await WaitForItemsAsync(list1, expected, $"round {round}: list 1 before the removes");
            await WaitForItemsAsync(list2, expected, $"round {round}: list 2 before the removes");

            // Each client reads its count the moment its remove completes, before it applies anything else. A request
            // that completed with false (a Noop) must already see the winning change. When both win, the other client's
            // change may still be on its way, so nothing is checked.
            var results = await Task.WhenAll(
                list1.RemoveAtWithCountAsync(0).AsTask(),
                list2.RemoveAtWithCountAsync(0).AsTask()).Timeout(T(1));
            var serverCount = serverList.Count;
            if (!results[0].Ok || !results[1].Ok)
                noops++;

            using (Assert.Multiple())
            {
                await Assert.That(results[0].Ok || results[1].Ok).IsTrue().Because($"round {round}: one request wins");
                if (!results[0].Ok)
                    await Assert.That(results[0].Count).IsEqualTo(serverCount).Because($"round {round}: list 1 got its Noop before the winning change");
                if (!results[1].Ok)
                    await Assert.That(results[1].Count).IsEqualTo(serverCount).Because($"round {round}: list 2 got its Noop before the winning change");
            }
        }

        await Assert.That(noops).IsGreaterThan(0).Because("at least one round must produce a Noop");

        await list1.DisableAsync().AsTask().Timeout(T(1));
        await list2.DisableAsync().AsTask().Timeout(T(1));
        await MatrixTestBase.EndAsync(client1);
        await MatrixTestBase.EndAsync(client2);
    }
}
