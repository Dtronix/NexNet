using NexNet.Invocation;
using NexNet.InteropServer;
using NexNet.TestNexus.Interop;

namespace NexNet.Interop.SelfTest;

/// <summary>
/// The proof of the harness (H04 step 5): one invocation, one pipe and one collection test written against
/// <see cref="IClientUnderTest"/>, run on every row of the client matrix (the csharp row when NEXNET_TEST_LANGS is unset).
/// </summary>
public class ProofTests
{
    private const double Wait = 10;

    private static Task<InteropServer<InteropServerNexus, InteropServerNexus.ClientProxy>> StartServer()
        => InteropServerHost.StartAsync<InteropServerNexus, InteropServerNexus.ClientProxy>(() => new InteropServerNexus());

    private static async Task<IClientUnderTest> Connect(ClientTarget target, InteropEndpoints endpoints)
    {
        var client = await Harness.CreateClientAsync(target, ClientSpec.ForEndpoint(endpoints, target.Transport));
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(Wait));
        return client;
    }

    private static async Task AssertNoLeaks(IClientUnderTest client)
    {
        var report = await client.EndTestAuditAsync();
        await Assert.That(report.IsClean).IsTrue().Because(report.ToString());
    }

    [Test]
    [ClientMatrix]
    public async Task Invocation_EchoesErrorsAndCallbacks(ClientTarget target)
    {
        await using var server = await StartServer();
        var client = await Connect(target, server.Endpoints);
        await using (client)
        {
            var proxy = client.Proxy<IInteropServerNexus>();
            await Assert.That(await proxy.EchoString("hi")).IsEqualTo("hi");
            await Assert.That(await proxy.EchoString(null)).IsNull();
            await Assert.That(await proxy.EchoInt64(long.MinValue)).IsEqualTo(long.MinValue);
            var combined = await proxy.Combine("n", 3, new InteropStruct { X = 1, Y = 2 });
            await Assert.That(combined.Name).IsEqualTo("n");
            await Assert.That(combined.Count).IsEqualTo(3);

            var error = await Assert.ThrowsExactlyAsync<ProxyRemoteInvocationException>(async () => await proxy.ThrowWithResult("boom"));
            await Assert.That(error).IsNotNull();

            // Fire and forget: the barrier waits for the send; the server records it on its own schedule.
            proxy.Notify("one");
            await client.BarrierAsync();
            await Assert.That(await proxy.WaitNotificationsAsync(1, TimeSpan.FromSeconds(Wait))).IsEquivalentTo(new[] { "one" });

            // Client callbacks run the script: unscripted, then Returns once, then Forward.
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

            // The connect lifecycle slot saw the first connection.
            await Assert.That((await script.OnConnected.NextCallAsync()).IsReconnected).IsFalse();

            await client.DisconnectAsync();
            await Assert.That(await client.GetStateAsync()).IsEqualTo(ConnectionState.Disconnected);
            await AssertNoLeaks(client);
        }
    }

    [Test]
    [ClientMatrix]
    public async Task Pipe_EchoesPatternAndChannel(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var client = await Connect(target, server.Endpoints);
        var proxy = client.Proxy<Remote_IInteropServerNexus>();

        const int length = 300_000; // several frames and pipe windows
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
    [ClientMatrix]
    public async Task Collection_SyncsBothWays(ClientTarget target)
    {
        await using var server = await StartServer();
        await using var first = await Connect(target, server.Endpoints);
        await using var second = await Connect(target, server.Endpoints);

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
}
