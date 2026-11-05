using System.Buffers;
using System.IO.Pipelines;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Pipes;
using NexNet.Serialization;
using NexNet.Transports;
using NUnit.Framework;

#pragma warning disable VSTHRD200
// CountedItem has a hand-written formatter, registered by the test that uses it.
#pragma warning disable NEXNET038

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// The built-in relay (<c>ForwardToAsync</c>, <c>BridgeAsync</c>): client A → server → client B. Client A opens the
/// source pipe to the server, client B the destination pipe; the test drives the forwarder on the server's ends.
/// </summary>
internal class PipeForwardingTests : BaseTests
{
    private Relay? _relay;

    [TearDown]
    public void ReleaseRelay()
    {
        // Lets the server invocations holding the relayed pipes return.
        _relay?.Done.TrySetResult();
        _relay = null;
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task ForwardToAsync_Relays64MiBIntactAndHalfClosesTheDestination(Type type)
    {
        const int total = 64 * 1024 * 1024;
        var r = await SetupRelay(type);

        var forward = r.ServerSource.ForwardToAsync(r.ServerDestination).AsTask();
        var writing = Task.Run(async () =>
        {
            await WritePattern(r.APipe.Output, total, seed: 0);
            await r.APipe.Output.CompleteAsync();
        });

        // B reads every byte in order, then sees its input complete: the forwarder completed the destination's output.
        Assert.That(await ReadPattern(r.BPipe.Input, seed: 0).Timeout(120), Is.EqualTo(total));
        await writing.Timeout(5);
        await forward.Timeout(5);

        // Half-close: the other direction of the destination pipe is still open.
        await r.BPipe.Output.WriteAsync(new byte[] { 1, 2, 3 }).Timeout(1);
        var back = await r.ServerDestination.Input.ReadAsync().AsTask().Timeout(1);
        Assert.That(back.Buffer.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        r.ServerDestination.Input.AdvanceTo(back.Buffer.End);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task ForwardToAsync_SlowDestination_StallsTheSourceInsteadOfBufferingOnTheServer(Type type)
    {
        const int total = 8 * 1024 * 1024;
        var r = await SetupRelay(type);
        var window = r.ServerConfig.PipeWindow;

        var forward = r.ServerSource.ForwardToAsync(r.ServerDestination).AsTask();
        var writing = Task.Run(async () =>
        {
            await WritePattern(r.APipe.Output, total, seed: 7);
            await r.APipe.Output.CompleteAsync();
        });

        // B reads nothing: A ends up waiting for send credit.
        TimeSpan? waiting = null;
        for (var i = 0; i < 100 && waiting == null; i++)
        {
            await Task.Delay(50);
            waiting = r.A.GetSnapshot()!.Pipes.Single().WaitingForCredit;
        }

        Assert.That(waiting, Is.Not.Null, "The source's writer never waited for send credit.");
        Assert.That(writing.IsCompleted, Is.False);

        // The server holds at most the source's window plus the destination's window (in flight to B).
        var buffered = r.Server.GetSnapshot().Sessions.SelectMany(s => s.Pipes).Sum(p => p.Buffered);
        Assert.That(buffered, Is.LessThanOrEqualTo(2L * window));

        // B drains: everything arrives in order and the transfer finishes.
        Assert.That(await ReadPattern(r.BPipe.Input, seed: 7).Timeout(60), Is.EqualTo(total));
        await writing.Timeout(5);
        await forward.Timeout(5);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task ForwardToAsync_Canceled_StopsPromptlyWithoutCompletingEitherPipe(Type type)
    {
        var r = await SetupRelay(type);
        using var cts = new CancellationTokenSource();

        var forward = r.ServerSource.ForwardToAsync(r.ServerDestination, cts.Token).AsTask();
        await r.APipe.Output.WriteAsync(new byte[1000]).Timeout(1);
        Assert.That(await ReadExactly(r.BPipe.Input, 1000).Timeout(2), Is.EqualTo(1000));

        // The forwarder is now waiting on the source's input.
        cts.Cancel();
        await Assert.ThatAsync(async () => await forward.Timeout(1), Throws.InstanceOf<OperationCanceledException>());

        // The source is still open: the server reads what A sends next.
        await r.APipe.Output.WriteAsync(new byte[] { 9, 9 }).Timeout(1);
        var read = await r.ServerSource.Input.ReadAsync().AsTask().Timeout(1);
        Assert.That(read.IsCompleted, Is.False);
        Assert.That(read.Buffer.ToArray(), Is.EqualTo(new byte[] { 9, 9 }));
        r.ServerSource.Input.AdvanceTo(read.Buffer.End);

        // The destination is still open: B receives what the server writes next.
        await r.ServerDestination.Output.WriteAsync(new byte[] { 4, 5 }).Timeout(1);
        var atB = await r.BPipe.Input.ReadAsync().AsTask().Timeout(1);
        Assert.That(atB.IsCompleted, Is.False);
        Assert.That(atB.Buffer.ToArray(), Is.EqualTo(new byte[] { 4, 5 }));
        r.BPipe.Input.AdvanceTo(atB.Buffer.End);
    }

    [TestCase(Type.Uds, true)]
    [TestCase(Type.Uds, false)]
    [TestCase(Type.Tcp, true)]
    [TestCase(Type.Tcp, false)]
    public async Task BridgeAsync_ForwardsBothDirectionsAndReturnsWhenBothFinished(Type type, bool aFinishesFirst)
    {
        const int total = 1024 * 1024;
        var r = await SetupRelay(type);

        var bridge = r.ServerSource.BridgeAsync(r.ServerDestination).AsTask();

        var (first, firstPeer, second, secondPeer) = aFinishesFirst
            ? (r.APipe, r.BPipe, r.BPipe, r.APipe)
            : (r.BPipe, r.APipe, r.APipe, r.BPipe);

        // One direction finishes: its reader sees all the data, then its input complete.
        var writing = Task.Run(async () =>
        {
            await WritePattern(first.Output, total, seed: 1);
            await first.Output.CompleteAsync();
        });
        Assert.That(await ReadPattern(firstPeer.Input, seed: 1).Timeout(30), Is.EqualTo(total));
        await writing.Timeout(5);

        // The other direction is still open, so the bridge keeps running.
        await Task.Delay(100);
        Assert.That(bridge.IsCompleted, Is.False);

        writing = Task.Run(async () =>
        {
            await WritePattern(second.Output, total, seed: 2);
            await second.Output.CompleteAsync();
        });
        Assert.That(await ReadPattern(secondPeer.Input, seed: 2).Timeout(30), Is.EqualTo(total));
        await writing.Timeout(5);

        await bridge.Timeout(5);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task BridgeAsync_SessionOfOnePipeDisconnects_CompletesTheOtherPipe(Type type)
    {
        var r = await SetupRelay(type);

        var bridge = r.ServerSource.BridgeAsync(r.ServerDestination).AsTask();
        await r.APipe.Output.WriteAsync(new byte[] { 1 }).Timeout(1);
        Assert.That(await ReadExactly(r.BPipe.Input, 1).Timeout(2), Is.EqualTo(1));

        // A's session ends abnormally: the bridge completes B's pipe and returns.
        await r.A.DisconnectAsync().Timeout(2);

        await r.BPipe.CompleteTask.Timeout(5);
        await bridge.Timeout(5);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task ChannelForwardToAsync_Relays100kItemsInOrder(Type type)
    {
        const int count = 100_000;
        var r = await SetupRelay(type);

        // Mixed sizes so the length prefixes are one, two and three bytes and split across reads.
        var items = Enumerable.Range(0, count)
            .Select(i => i % 1000 == 0 ? new byte[20_000] : i % 10 == 0 ? new byte[300] : new byte[i % 50])
            .ToArray();
        for (var i = 0; i < count; i++)
        {
            if (items[i].Length > 0)
                BitConverter.TryWriteBytes(items[i].AsSpan(0, Math.Min(4, items[i].Length)), i);
        }

        var source = r.ServerSource.GetChannel<byte[]>();
        var destination = r.ServerDestination.GetChannel<byte[]>();
        var forward = source.ForwardToAsync(destination).AsTask();

        var writing = Task.Run(async () => await r.APipe.GetChannel<byte[]>().WriteAndComplete(items, chunkSize: 500));
        var received = await r.BPipe.GetChannel<byte[]>().ReadUntilComplete(count).AsTask().Timeout(120);

        await writing.Timeout(5);
        await forward.Timeout(5);
        Assert.That(received.Count, Is.EqualTo(count));
        for (var i = 0; i < count; i++)
        {
            if (!received[i].AsSpan().SequenceEqual(items[i]))
                Assert.Fail($"Item {i} differs.");
        }
    }

    [Test]
    public async Task ChannelForwardToAsync_DoesNotSerializeOrDeserializeOnTheRelay()
    {
        const int count = 1000;
        NexusFormatterRegistry.Register(new CountingFormatter());
        CountingFormatter.Reset();
        var r = await SetupRelay(Type.Uds);

        var forward = r.ServerSource.GetChannel<CountedItem>()
            .ForwardToAsync(r.ServerDestination.GetChannel<CountedItem>()).AsTask();

        var items = Enumerable.Range(0, count).Select(i => new CountedItem { Sequence = i }).ToArray();
        var writing = Task.Run(async () => await r.APipe.GetChannel<CountedItem>().WriteAndComplete(items, chunkSize: 100));
        var received = await r.BPipe.GetChannel<CountedItem>().ReadUntilComplete(count).AsTask().Timeout(30);
        await writing.Timeout(5);
        await forward.Timeout(5);

        Assert.That(received.Select(i => i.Sequence), Is.EqualTo(Enumerable.Range(0, count)));

        // A serializes each item once and B deserializes it once; a relay that re-typed the items would double both.
        Assert.That(CountingFormatter.Serialized, Is.EqualTo(count));
        Assert.That(CountingFormatter.Deserialized, Is.EqualTo(count));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task ChannelForwardToAsync_ItemTooLargeForTheDestination_ThrowsBeforeWritingItAndLeavesBothOpen(Type type)
    {
        // B accepts 64 KiB, so the server's session to B sends at most that; A's session to the server accepts more.
        var r = await SetupRelay(type, configureB: config =>
        {
            config.MaxMessageSize = 64 * 1024;
            config.SessionBudget = 64 * 1024 * 1024;
        });

        var source = r.ServerSource.GetChannel<byte[]>();
        var destination = r.ServerDestination.GetChannel<byte[]>();
        var forward = source.ForwardToAsync(destination).AsTask();

        var aWriter = await r.APipe.GetChannel<byte[]>().GetWriterAsync();
        await aWriter.WriteAsync(new byte[10]).Timeout(1);
        await aWriter.WriteAsync(new byte[100 * 1024]).Timeout(5);
        await aWriter.WriteAsync(new byte[20]).Timeout(5);

        var bItems = (await r.BPipe.GetChannel<byte[]>().GetReaderAsync()).GetAsyncEnumerator();
        Assert.That(await bItems.MoveNextAsync().AsTask().Timeout(2), Is.True);
        Assert.That(bItems.Current.Length, Is.EqualTo(10));

        await Assert.ThatAsync(async () => await forward.Timeout(5), Throws.InstanceOf<NexusMessageTooLargeException>());

        // None of the oversized item reached B: the next item B reads is the one the server writes now.
        var serverWriter = await destination.GetWriterAsync();
        await serverWriter.WriteAsync(new byte[5]).Timeout(1);
        Assert.That(await bItems.MoveNextAsync().AsTask().Timeout(2), Is.True);
        Assert.That(bItems.Current.Length, Is.EqualTo(5));

        // The source is still open and positioned at the oversized item, for the caller to decide.
        var sourceItems = (await source.GetReaderAsync()).GetAsyncEnumerator();
        Assert.That(await sourceItems.MoveNextAsync().AsTask().Timeout(2), Is.True);
        Assert.That(sourceItems.Current.Length, Is.EqualTo(100 * 1024));
        Assert.That(await sourceItems.MoveNextAsync().AsTask().Timeout(2), Is.True);
        Assert.That(sourceItems.Current.Length, Is.EqualTo(20));
    }

    private async Task<RelaySetup> SetupRelay(Type type, Action<ClientConfig>? configureB = null)
    {
        var relay = _relay = new Relay();
        var serverConfig = CreateServerConfig(type);
        serverConfig.SessionBudget = 64 * 1024 * 1024;
        var server = CreateServer(serverConfig, nexus =>
            nexus.ServerTaskValueWithDuplexPipeEvent = (_, pipe) => relay.OnPipe(pipe));
        await server.StartAsync().Timeout(1);

        var (a, _) = CreateClient(CreateClientConfig(type));
        await a.ConnectAsync().Timeout(1);
        var bConfig = CreateClientConfig(type);
        configureB?.Invoke(bConfig);
        var (b, _) = CreateClient(bConfig);
        await b.ConnectAsync().Timeout(1);

        // A's pipe reaches the server first and is the source; B's is the destination.
        var aPipe = a.CreatePipe();
        await a.Proxy.ServerTaskValueWithDuplexPipe(aPipe).Timeout(1);
        await aPipe.ReadyTask.Timeout(1);
        var serverSource = await relay.Source.Task.Timeout(1);

        var bPipe = b.CreatePipe();
        await b.Proxy.ServerTaskValueWithDuplexPipe(bPipe).Timeout(1);
        await bPipe.ReadyTask.Timeout(1);
        var serverDestination = await relay.Destination.Task.Timeout(1);
        await serverSource.ReadyTask.Timeout(1);
        await serverDestination.ReadyTask.Timeout(1);

        return new RelaySetup(server, serverConfig, a, aPipe, serverSource, b, bPipe, serverDestination);
    }

    /// <summary>
    /// Writes <paramref name="total"/> bytes of a pattern that depends on each byte's offset.
    /// </summary>
    private static async Task WritePattern(PipeWriter output, int total, int seed)
    {
        var chunk = new byte[64 * 1024];
        for (var offset = 0; offset < total; offset += chunk.Length)
        {
            var length = Math.Min(chunk.Length, total - offset);
            for (var i = 0; i < length; i++)
                chunk[i] = PatternByte(offset + i, seed);
            var result = await output.WriteAsync(chunk.AsMemory(0, length));
            if (result.IsCompleted || result.IsCanceled)
                throw new InvalidOperationException($"Write stopped at {offset}.");
        }
    }

    /// <summary>
    /// Reads until the input completes, checking every byte against the pattern. Returns the bytes read.
    /// </summary>
    private static async Task<long> ReadPattern(PipeReader input, int seed)
    {
        long offset = 0;
        while (true)
        {
            var result = await input.ReadAsync();
            foreach (var segment in result.Buffer)
            {
                var span = segment.Span;
                for (var i = 0; i < span.Length; i++)
                {
                    if (span[i] != PatternByte(offset + i, seed))
                        throw new InvalidOperationException($"Byte {offset + i} is out of order or corrupt.");
                }

                offset += span.Length;
            }

            input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                return offset;
        }
    }

    private static async Task<long> ReadExactly(PipeReader input, int count)
    {
        long read = 0;
        while (read < count)
        {
            var result = await input.ReadAsync();
            read += result.Buffer.Length;
            input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                break;
        }

        return read;
    }

    private static byte PatternByte(long offset, int seed) => (byte)((offset + seed) % 251);

    private sealed record RelaySetup(
        NexusServer<ServerNexus, ServerNexus.ClientProxy> Server,
        ServerConfig ServerConfig,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> A,
        INexusDuplexPipe APipe,
        INexusDuplexPipe ServerSource,
        NexusClient<ClientNexus, ClientNexus.ServerProxy> B,
        INexusDuplexPipe BPipe,
        INexusDuplexPipe ServerDestination);

    /// <summary>
    /// Pairs the server's ends of the two pipes and keeps their invocations running until the test ends.
    /// </summary>
    private sealed class Relay
    {
        public readonly TaskCompletionSource<INexusDuplexPipe> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<INexusDuplexPipe> Destination = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async ValueTask OnPipe(INexusDuplexPipe pipe)
        {
            var slot = Interlocked.Increment(ref _arrived) == 1 ? Source : Destination;
            slot.TrySetResult(pipe);
            await Done.Task.ConfigureAwait(false);
        }
    }

    internal sealed class CountedItem
    {
        public int Sequence;
    }

    /// <summary>
    /// Counts every call, in every process-local session: client A, the server and client B share it.
    /// </summary>
    private sealed class CountingFormatter : NexusFormatter<CountedItem>
    {
        private static int _serialized;
        private static int _deserialized;

        public static int Serialized => Volatile.Read(ref _serialized);
        public static int Deserialized => Volatile.Read(ref _deserialized);

        public static void Reset()
        {
            Volatile.Write(ref _serialized, 0);
            Volatile.Write(ref _deserialized, 0);
        }

        public override void Serialize(ref MsgPackWriter writer, CountedItem? value)
        {
            Interlocked.Increment(ref _serialized);
            writer.Write(value!.Sequence);
        }

        public override void Deserialize(ref MsgPackReader reader, ref CountedItem? value)
        {
            Interlocked.Increment(ref _deserialized);
            value = new CountedItem { Sequence = reader.ReadInt32() };
        }
    }
}
