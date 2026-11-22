using NexNet.Collections;
using NexNet.Interop.Hosts;
using NexNet.Interop.Remote;
using NexNet.Interop.Tests.Fake;
using NexNet.Invocation;
using NexNet.InteropServer;
using NexNet.Messages;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.Tests;

/// <summary>
/// The remote driver end to end: test, RemoteClientDriver, control protocol, fake host, C# client, interop server. The
/// same scenarios as the H04 proof tests, so the protocol carries everything the harness API needs.
/// </summary>
public class RemoteDriverTests
{
    private const double Wait = 10;

    private static readonly LanguageHostOptions Options = new() { RegisterLanguage = false };

    private static Task<InteropServer<InteropServerNexus, InteropServerNexus.ClientProxy>> StartServer()
        => InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus());

    private static async Task<LanguageHost> StartHost()
    {
        var host = new LanguageHost("fake", FakeHostLauncher.Config(), new FakeHostLauncher(), Options);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return host;
    }

    private static async Task<IClientUnderTest> Connect(LanguageHost host, InteropEndpoints endpoints, ClientTransport transport = ClientTransport.Tcp)
    {
        var client = await host.DriverFactory.CreateAsync<InteropClientNexus, InteropClientNexus.ServerProxy>(
            new ClientTarget("fake", transport), ClientSpec.ForEndpoint(endpoints, transport));
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        return client;
    }

    private static async Task AssertNoLeaks(IClientUnderTest client)
    {
        var report = await client.EndTestAuditAsync();
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }

    [Test]
    public async Task Invocation_EchoesErrorsCancellationAndScripts()
    {
        await using var server = await StartServer();
        await using var host = await StartHost();
        var client = await Connect(host, server.Endpoints, ClientTransport.WebSocketTls);
        await using (client)
        {
            await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Connected);
            var proxy = client.Proxy<IInteropServerNexus>();
            await Assert.That(await proxy.EchoString("hi")).IsEqualTo("hi");
            await Assert.That(await proxy.EchoString(null)).IsNull();
            await Assert.That(await proxy.EchoInt64(long.MinValue)).IsEqualTo(long.MinValue);
            var combined = await proxy.Combine("n", 3, new InteropStruct { X = 1, Y = 2 });
            await Assert.That(combined.Count).IsEqualTo(3);
            await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(async () => await proxy.ThrowWithResult("boom"));

            // Cancellation: a host-side timer, a token canceled from the test, and a token canceled before sending.
            await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await client.Proxy<Remote_IInteropServerNexus>().Delay(5000, 1, RemoteCancel.After(100)));
            using (var cts = new CancellationTokenSource(150))
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await proxy.Delay(5000, 2, cts.Token));
            using (var canceled = new CancellationTokenSource())
            {
                await canceled.CancelAsync();
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await proxy.Delay(10, 3, canceled.Token));
            }

            // Fire and forget: the barrier waits for the sends; the server records them on its own schedule.
            proxy.Notify("one");
            proxy.Notify("two");
            await client.BarrierAsync();
            await Assert.That(await proxy.WaitNotificationsAsync(2, TimeSpan.FromSeconds(Wait))).IsEquivalentTo(new[] { "one", "two" });

            var script = client.Callbacks<InteropClientNexusScript>();
            await Assert.That(await proxy.CallClientEchoString("x")).IsNull();
            await Assert.That((await script.ClientEchoString.NextCallAsync()).value).IsEqualTo("x");

            script.ClientEchoString.Returns("scripted").Times(1);
            await Assert.That(await proxy.CallClientEchoString("y")).IsEqualTo("scripted");
            await Assert.That(await proxy.CallClientEchoString("z")).IsNull();

            script.ClientEchoString.Clear();
            script.ClientEchoString.AwaitSignal("go").Forward(a => new ValueTask<string?>("fwd:" + a.value));
            var pending = proxy.CallClientEchoString("w").AsTask();
            await Assert.That((await script.ClientEchoString.NextCallAsync()).value).IsEqualTo("y");
            await Assert.That((await script.ClientEchoString.NextCallAsync()).value).IsEqualTo("z");
            await Assert.That((await script.ClientEchoString.NextCallAsync()).value).IsEqualTo("w");
            await Assert.That(pending.IsCompleted).IsFalse();
            await client.SignalAsync("go");
            await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(Wait))).IsEqualTo("fwd:w");

            script.ClientEchoString.Clear();
            script.ClientEchoString.Throws(ErrorKind.InvalidOperation, "nope");
            await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(async () => await proxy.CallClientEchoString("v"));

            await Assert.That((await script.OnConnected.NextCallAsync()).IsReconnected).IsFalse();

            await client.DisconnectAsync();
            await Assert.That(await client.WhenDisconnectedAsync(Wait)).IsEqualTo(DisconnectReason.Graceful);
            await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
            await AssertNoLeaks(client);
        }
    }

    [Test]
    public async Task Pipe_EchoesPatternBytesAndChannel()
    {
        await using var server = await StartServer();
        await using var host = await StartHost();
        await using var client = await Connect(host, server.Endpoints);
        var proxy = client.Proxy<Remote_IInteropServerNexus>();

        const int length = 300_000;
        await using (var pipe = await client.CreatePipeAsync())
        {
            var call = proxy.EchoPipe(pipe).AsTask();
            await pipe.WhenReadyAsync();
            var drain = pipe.DrainAsync().AsTask();
            await pipe.WritePatternAsync(7, length);
            await pipe.CompleteAsync();
            await Assert.That(await drain.WaitAsync(TimeSpan.FromSeconds(Wait))).IsEqualTo(PipePattern.Digest(length, 7));
            await call.WaitAsync(TimeSpan.FromSeconds(Wait));
        }

        await using (var pipe = await client.CreatePipeAsync())
        {
            var call = proxy.EchoPipe(pipe).AsTask();
            await pipe.WhenReadyAsync();
            await pipe.WriteAsync(new byte[] { 1, 2, 3, 4 });
            var echoed = await pipe.ReadAsync(4, 16);
            await Assert.That(echoed.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
            await pipe.CompleteAsync();
            await call.WaitAsync(TimeSpan.FromSeconds(Wait));
            await pipe.WhenCompletedAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(Wait));
        }

        await using (var channelPipe = await client.CreatePipeAsync())
        {
            var call = proxy.EchoItemChannel(channelPipe).AsTask();
            await channelPipe.WhenReadyAsync();
            var writer = await channelPipe.GetChannelWriterAsync<InteropItem>();
            var reader = await channelPipe.GetChannelReaderAsync<InteropItem>();
            var items = Enumerable.Range(0, 50).Select(i => new InteropItem(i, "item " + i)).ToArray();
            await writer.WriteManyAsync(items);
            await writer.CompleteAsync();
            var echoed = await reader.ReadAllAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(Wait));
            await Assert.That(echoed).IsEquivalentTo(items, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await call.WaitAsync(TimeSpan.FromSeconds(Wait));
        }

        await client.DisconnectAsync();
        await AssertNoLeaks(client);
    }

    [Test]
    public async Task Collection_SyncsBothWays()
    {
        await using var server = await StartServer();
        await using var host = await StartHost();
        await using var first = await Connect(host, server.Endpoints);
        await using var second = await Connect(host, server.Endpoints);

        var a = first.List<InteropItem>("Items");
        var b = second.Proxy<Remote_IInteropServerNexus>().Items;
        await Assert.That(await a.EnableAsync()).IsTrue();
        await Assert.That(await b.EnableAsync()).IsTrue();

        await Assert.That(await a.AddAsync(new InteropItem(1, "one"))).IsTrue();
        await Assert.That(await b.AddAsync(new InteropItem(2, "two"))).IsTrue();
        var expected = new[] { new InteropItem(1, "one"), new InteropItem(2, "two") };
        await Assert.That(await a.WaitForAsync(l => l.Count == 2, TimeSpan.FromSeconds(Wait))).IsEquivalentTo(expected);
        await Assert.That(await b.WaitForAsync(l => l.Count == 2, TimeSpan.FromSeconds(Wait))).IsEquivalentTo(expected);

        await Assert.That(await a.MoveAsync(0, 1)).IsTrue();
        await Assert.That(await a.RemoveAtAsync(0)).IsTrue();
        await Assert.That(await b.WaitForAsync(l => l.Count == 1 && l[0].Id == 1, TimeSpan.FromSeconds(Wait))).Count().IsEqualTo(1);
        await Assert.That(await a.SnapshotAsync()).IsEquivalentTo(new[] { new InteropItem(1, "one") });

        await a.DisableAsync();
        await b.DisableAsync();
        await first.DisconnectAsync();
        await second.DisconnectAsync();
        await AssertNoLeaks(first);
        await AssertNoLeaks(second);
    }

    [Test]
    public async Task Collection_WatchReportsChangeEvents_AndArgumentErrors()
    {
        await using var server = await StartServer();
        await using var host = await StartHost();
        await using var first = await Connect(host, server.Endpoints);
        await using var second = await Connect(host, server.Endpoints);

        var a = first.List<InteropItem>("Items");
        var b = second.Proxy<Remote_IInteropServerNexus>().Items;
        await Assert.That(await a.EnableAsync()).IsTrue();
        await Assert.That(await b.EnableAsync()).IsTrue();

        var watch = await b.WatchAsync();
        await Assert.That(await a.AddAsync(new InteropItem(1, "one"))).IsTrue();
        await Assert.That(await a.AddAsync(new InteropItem(2, "two"))).IsTrue();
        await watch.WaitForAsync(NexusCollectionChangedAction.Add, 2, TimeSpan.FromSeconds(Wait));
        await Assert.That(await a.MoveAsync(0, 1)).IsTrue();
        await watch.WaitForAsync(NexusCollectionChangedAction.Move, 1, TimeSpan.FromSeconds(Wait));
        await Assert.That(await a.ReplaceAsync(0, new InteropItem(3, "three"))).IsTrue();
        await watch.WaitForAsync(NexusCollectionChangedAction.Replace, 1, TimeSpan.FromSeconds(Wait));
        await Assert.That(await a.RemoveAtAsync(0)).IsTrue();
        await watch.WaitForAsync(NexusCollectionChangedAction.Remove, 1, TimeSpan.FromSeconds(Wait));
        await Assert.That(await a.ClearAsync()).IsTrue();
        var changes = await watch.WaitForAsync(NexusCollectionChangedAction.Reset, 1, TimeSpan.FromSeconds(Wait));

        await Assert.That(changes.Select(c => c.Action)).IsEquivalentTo(
            new[]
            {
                NexusCollectionChangedAction.Add, NexusCollectionChangedAction.Add, NexusCollectionChangedAction.Move,
                NexusCollectionChangedAction.Replace, NexusCollectionChangedAction.Remove, NexusCollectionChangedAction.Reset,
            },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        for (var i = 1; i < changes.Count; i++)
            await Assert.That(changes[i].HostTime).IsGreaterThanOrEqualTo(changes[i - 1].HostTime);

        // A watch still attached is a leaked subscription.
        var leaked = await second.EndTestAuditAsync();
        await Assert.That(leaked.Subscriptions).IsEqualTo(1);
        await watch.DisposeAsync();
        await Assert.That(await b.WaitForAsync(l => l.Count == 0, TimeSpan.FromSeconds(Wait))).Count().IsEqualTo(0);

        // Out-of-range indexes are argument errors on every row.
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await a.InsertAsync(-1, new InteropItem(4, "four")));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await a.RemoveAtAsync(-1));

        await a.DisableAsync();
        await b.DisableAsync();
        await first.DisconnectAsync();
        await second.DisconnectAsync();
        await AssertNoLeaks(first);
        await AssertNoLeaks(second);
    }

    [Test]
    public async Task LeakReport_CountsOpenClientsAndPipes_AndTestEndFailsOnALeak()
    {
        await using var server = await StartServer();
        await using var host = await StartHost();
        var client = (RemoteClientDriver)await Connect(host, server.Endpoints);
        var pipe = await client.CreatePipeAsync();

        var report = await client.EndTestAuditAsync();
        await Assert.That(report.OpenClients).IsEqualTo(1);
        await Assert.That(report.OpenPipes).IsEqualTo(1);
        await Assert.That(report.Details.All(d => d.StartsWith(client.Handle + ": ", StringComparison.Ordinal))).IsTrue();

        // test.end reports the leak, fails, and releases everything of the scope.
        var leak = await Assert.ThrowsExactlyAsync<LeakDetectedException>(
            async () => await RemoteTestAudit.EndAsync(client.Connection, "fake", client.Test));
        await Assert.That(leak!.Report.OpenClients).IsEqualTo(1);
        await Assert.That(leak.Language).IsEqualTo("fake");
        await Assert.That(client.Connection.HasScope(client.Test)).IsFalse();

        // Disposing after the scope ended is quiet: the host released them already.
        await pipe.DisposeAsync();
        await client.DisposeAsync();
    }

    [Test]
    public async Task ACleanTest_EndsWithACleanReport()
    {
        await using var server = await StartServer();
        await using var host = await StartHost();
        var client = (RemoteClientDriver)await Connect(host, server.Endpoints);
        await client.DisconnectAsync();
        await client.DisposeAsync();

        var report = await RemoteTestAudit.EndAsync(client.Connection, "fake", client.Test);
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.IsClean).IsTrue();
        await Assert.That(client.Test).IsEqualTo(TestContext.Current!.Id);
    }
}
