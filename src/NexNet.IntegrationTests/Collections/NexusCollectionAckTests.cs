using System.Diagnostics;
using NexNet.Collections;
using NexNet.IntegrationTests.Collections.Lists;
using NexNet.Interop;
using NexNet.Interop.Hosts;

namespace NexNet.IntegrationTests.Collections;

/// <summary>Acknowledgments of the client under test's list operations.</summary>
[NotInParallel]
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusCollectionAckTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task UpdateAndWaitAsync_CompletesOnAcknowledgment(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        var stopwatch = Stopwatch.StartNew();
        var result = await list.AddAsync(42).Timeout(T(1));
        stopwatch.Stop();

        await Assert.That(result).IsTrue();
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(1000);
        await Assert.That((await ItemsAsync(list)).Contains(42)).IsTrue();
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task UpdateAndWaitAsync_TimesOutOnNoAcknowledgment(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // Disable acknowledgments to simulate timeout
        DoNotSendAck(s.ServerList);

        var stopwatch = Stopwatch.StartNew();
        var addTask = list.AddAsync(42).AsTask();

        // Should not complete within reasonable time
        var completedTask = await Task.WhenAny(addTask, Task.Delay(100));
        stopwatch.Stop();

        await Assert.That(ReferenceEquals(completedTask, addTask)).IsFalse();
        await Assert.That(stopwatch.ElapsedMilliseconds).IsGreaterThan(90);

        // Disconnecting ends the wait, so the client holds nothing after the test.
        await s.Client.DisconnectAsync().Timeout(T(1));
        await addTask.Timeout(T(1));
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task MultipleOperations_ReceiveCorrectAcknowledgments(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        // Send multiple operations concurrently
        var tasks = new List<Task<bool>>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(list.AddAsync(i).AsTask());
        }

        var results = await Task.WhenAll(tasks).Timeout(T(2));

        await Assert.That(results).All(x => x);
        await Assert.That((await ItemsAsync(list)).Length).IsEqualTo(10);
        await s.EndAsync();
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task DisconnectDuringOperation_CompletesWithFalse(ClientTarget target)
    {
        await using var s = await ConnectAsync(target);
        var list = s.List;
        await list.EnableAsync().Timeout(T(1));

        DoNotSendAck(s.ServerList);

        using var serverAdded = s.ServerList.WaitForEvent(NexusCollectionChangedAction.Add);
        var addTask = list.AddAsync(42).AsTask();

        // Disconnect while operation is pending: the server has applied it and holds back the acknowledgment.
        await serverAdded.Wait();
        await s.Client.DisconnectAsync().Timeout(T(1));

        var result = await addTask.Timeout(T(1));
        await Assert.That(result).IsFalse();
        await s.EndAsync();
    }
}
