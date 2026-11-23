using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Internals.Pipelines;
using NexNet.IntegrationTests.Pipes;
using NexNet.Messages;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

/// <summary>
/// The transport tap seam (<see cref="ConfigBase.Tap"/>): what each side writes is what the other side reads, from the
/// preamble on, on every transport; with no tap the session uses the transport's own reader and writer.
/// </summary>
internal class TransportTapTests : BaseTests
{
    private static readonly byte[] Preamble = [0x4e, 0x6e, 0x50, 0x14, 0x00, 0x00, 0x00, 0x01];

    [Test]
    [Transports]
    public async Task CapturedStreams_MatchPeer(Type type)
    {
        var serverTap = new RecordingTap();
        var clientTap = new RecordingTap();
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        serverConfig.Tap = serverTap;
        clientConfig.Tap = clientTap;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        for (var i = 0; i < 20; i++)
            await Assert.That(await client.Proxy.ServerEcho([(byte)i]).Timeout(T(5))).IsEquivalentTo(new[] { (byte)i });

        // Larger than one frame: streamed in 64 KB chunks, so reads end mid-frame and leave a remainder to the next.
        var large = new byte[300 * 1024];
        Random.Shared.NextBytes(large);
        await Assert.That(await client.Proxy.ServerEcho(large).Timeout(T(10))).IsEquivalentTo(large);

        var serverSession = serverTap.Sessions.Single();
        var clientSession = clientTap.Sessions.Single();

        // Pings and credit can still be in flight: wait until both directions agree.
        await WaitUntil(() => serverSession.Inbound.SequenceEqual(clientSession.Outbound)
                              && clientSession.Inbound.SequenceEqual(serverSession.Outbound), 5);

        await Assert.That(serverSession.Inbound).IsEquivalentTo(clientSession.Outbound);
        await Assert.That(clientSession.Inbound).IsEquivalentTo(serverSession.Outbound);

        await Assert.That(clientSession.Outbound.Take(Preamble.Length).ToArray()).IsEquivalentTo(Preamble);
        await Assert.That(serverSession.Outbound.Take(Preamble.Length).ToArray()).IsEquivalentTo(Preamble);
        await Assert.That(clientSession.Outbound.Length).IsGreaterThan(large.Length);
        await Assert.That(serverSession.Inbound.Length).IsEqualTo(clientSession.Outbound.Length);

        await Assert.That(serverSession.Info.IsServer).IsTrue();
        await Assert.That(clientSession.Info.IsServer).IsFalse();
        await Assert.That(clientSession.Info.Id).IsEqualTo(0);
        await Assert.That(serverSession.ConnectedId).IsEqualTo(serverSession.Info.Id);
        await Assert.That(clientSession.ConnectedId).IsEqualTo(serverSession.Info.Id);

        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        await clientSession.Closed.Task.Timeout(T(5));
        await serverSession.Closed.Task.Timeout(T(5));

        // The client's disconnect frame is its last write; whatever the server read of it is a prefix of it.
        await Assert.That(clientSession.Outbound.AsSpan().StartsWith(serverSession.Inbound)).IsTrue();
        await Assert.That(clientSession.ClosedReason).IsEqualTo(DisconnectReason.Graceful);
        await Assert.That(clientSession.Outbound[^1]).IsEqualTo((byte)DisconnectReason.Graceful);
    }

    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task NoTap_SessionUsesTransportPipes(Type type)
    {
        var (serverView, clientView, _) = await ConnectAndCaptureViews(type, tap: null);

        await Assert.That(serverView.Input).IsSameReferenceAs(serverView.Transport.Input);
        await Assert.That(serverView.Output).IsSameReferenceAs(serverView.Transport.Output);
        await Assert.That(clientView.Input).IsSameReferenceAs(clientView.Transport.Input);
        await Assert.That(clientView.Output).IsSameReferenceAs(clientView.Transport.Output);
    }

    [Test]
    public async Task TapDecliningSession_SessionUsesTransportPipes()
    {
        var (serverView, clientView, tap) = await ConnectAndCaptureViews(Type.Uds, new DecliningTap());

        await Assert.That(((DecliningTap)tap!).Calls).IsEqualTo(2);
        await Assert.That(serverView.Input).IsSameReferenceAs(serverView.Transport.Input);
        await Assert.That(serverView.Output).IsSameReferenceAs(serverView.Transport.Output);
        await Assert.That(clientView.Input).IsSameReferenceAs(clientView.Transport.Input);
        await Assert.That(clientView.Output).IsSameReferenceAs(clientView.Transport.Output);
    }

    [Test]
    public async Task Tap_SessionUsesTapPipes()
    {
        var (serverView, clientView, _) = await ConnectAndCaptureViews(Type.Uds, new RecordingTap());

        await Assert.That(serverView.Input is TapPipeReader).IsTrue();
        await Assert.That(serverView.Output is TapPipeWriter).IsTrue();
        await Assert.That(clientView.Input is TapPipeReader).IsTrue();
        await Assert.That(clientView.Output is TapPipeWriter).IsTrue();
    }

    [Test]
    public async Task ThrowingTap_SessionKeepsWorking()
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        serverConfig.Tap = new ThrowingTap();
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(Type.Uds));

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await client.Proxy.ServerEcho([5]).Timeout(T(5))).IsEquivalentTo(new byte[] { 5 });
    }

    /// <summary>
    /// Off, the session uses the transport's own reader and writer (see the tests above), so it allocates exactly what
    /// it did without the seam. On with a tap that keeps nothing, calls still allocate what they do without one.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task NoOpTap_AddsNoSteadyStateAllocations()
    {
        var off = await AllocatedPerCall(tap: null);
        var on = await AllocatedPerCall(new NoOpTap());

        // Process-wide counting: leave room for the runtime's own background allocations.
        await Assert.That(on - off).IsLessThan(16);
        await Assert.That(off).IsLessThan(1024);
    }

    private async Task<double> AllocatedPerCall(INexusTap? tap)
    {
        // The least of a few rounds: process-wide counting also sees the runtime's own work (pool trimming after a GC).
        var least = double.MaxValue;
        for (var round = 0; round < 3; round++)
            least = Math.Min(least, await AllocatedPerCallOnce(tap));
        return least;
    }

    private async Task<double> AllocatedPerCallOnce(INexusTap? tap)
    {
        const int calls = 4000;
        var serverConfig = CreateServerConfig(Type.Uds, BasePipeTests.LogMode.None);
        var clientConfig = CreateClientConfig(Type.Uds, BasePipeTests.LogMode.None);
        serverConfig.Tap = tap;
        clientConfig.Tap = tap;
        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        byte[] payload = [1, 2, 3, 4];

        // Warm up: pools, JIT and tiering.
        for (var i = 0; i < calls; i++)
            await client.Proxy.ServerEcho(payload);

        var before = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < calls; i++)
            await client.Proxy.ServerEcho(payload);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;

        await client.DisconnectAsync().Timeout(T(TestTimeouts.Connect));
        await server.StopAsync().Timeout(T(TestTimeouts.Connect));
        return allocated / (double)calls;
    }

    // ---- Wrapper behavior, on an in-memory pipe ----

    [Test]
    public async Task Reader_ReportsOnlyNewBytes()
    {
        var pipe = new Pipe(InlineOptions);
        var tap = new SessionRecorder(default);
        var reader = new TapPipeReader(pipe.Reader, tap, null);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6 });
        var read = await reader.ReadAsync();
        await Assert.That(read.Buffer.Length).IsEqualTo(6);

        // Consume two, examine all: four stay for the next read.
        reader.AdvanceTo(read.Buffer.GetPosition(2), read.Buffer.End);
        await pipe.Writer.WriteAsync(new byte[] { 7, 8 });
        read = await reader.ReadAsync();
        await Assert.That(read.Buffer.Length).IsEqualTo(6);

        // Consume nothing (an incomplete header): the next read only adds what arrives.
        reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
        await pipe.Writer.WriteAsync(new byte[] { 9 });
        read = await reader.ReadAsync();
        reader.AdvanceTo(read.Buffer.End);

        await Assert.That(tap.Inbound).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        await Assert.That(tap.ReadSizes).IsEquivalentTo(new[] { 6, 2, 1 });
    }

    [Test]
    public async Task Reader_CanceledReadReportsNothing()
    {
        var pipe = new Pipe(InlineOptions);
        var tap = new SessionRecorder(default);
        var reader = new TapPipeReader(pipe.Reader, tap, null);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 });
        var read = await reader.ReadAsync();
        reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);

        reader.CancelPendingRead();
        read = await reader.ReadAsync();
        await Assert.That(read.IsCanceled).IsTrue();
        reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);

        await Assert.That(tap.ReadSizes).IsEquivalentTo(new[] { 3 });
    }

    [Test]
    public async Task Writer_ReportsCommittedBytesOncePerFlush()
    {
        var pipe = new Pipe(InlineOptions);
        var tap = new SessionRecorder(default);
        var writer = new TapPipeWriter(pipe.Writer, tap, null);

        // Several commits, one of them larger than a pipe segment (4 KB), then one flush: one write op.
        var expected = new List<byte>();
        for (var i = 0; i < 5; i++)
        {
            var size = i == 3 ? 6000 : 100 + i;
            var span = writer.GetSpan(size);
            for (var b = 0; b < size; b++)
                span[b] = (byte)(i * 31 + b);
            expected.AddRange(span[..size].ToArray());
            writer.Advance(size);
        }

        await writer.FlushAsync();
        await Assert.That(tap.WriteSizes).IsEquivalentTo(new[] { expected.Count });
        await Assert.That(tap.Outbound).IsEquivalentTo(expected.ToArray());

        // Two Advance calls on one buffer are one contiguous region.
        var memory = writer.GetMemory(10);
        memory.Span[..4].Fill(0xAA);
        writer.Advance(4);
        memory.Span.Slice(4, 3).Fill(0xBB);
        writer.Advance(3);
        await writer.FlushAsync();

        // A flush with nothing committed reports nothing.
        await writer.FlushAsync();

        await Assert.That(tap.WriteSizes).IsEquivalentTo(new[] { expected.Count, 7 });
        await Assert.That(tap.Outbound[^7..]).IsEquivalentTo(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xBB, 0xBB, 0xBB });

        // The peer receives exactly what was reported.
        var read = await pipe.Reader.ReadAsync();
        await Assert.That(read.Buffer.ToArray()).IsEquivalentTo(tap.Outbound);
    }

    [Test]
    public async Task Writer_BlockingTap_HoldsFlush()
    {
        var pipe = new Pipe(InlineOptions);
        var gate = new BlockingTap();
        var writer = new TapPipeWriter(pipe.Writer, gate, null);

        writer.GetSpan(3)[..3].Fill(7);
        writer.Advance(3);
        var flush = writer.FlushAsync().AsTask();

        await Assert.That(flush.IsCompleted).IsFalse();
        await Assert.That(pipe.Reader.TryRead(out _)).IsFalse();

        gate.Release();
        await flush.Timeout(T(1));
        await Assert.That(pipe.Reader.TryRead(out var read)).IsTrue();
        await Assert.That(read.Buffer.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Reader_BlockingTap_HoldsRead()
    {
        var pipe = new Pipe(InlineOptions);
        var gate = new BlockingTap();
        var reader = new TapPipeReader(pipe.Reader, gate, null);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2 });
        var read = reader.ReadAsync().AsTask();
        await Assert.That(read.IsCompleted).IsFalse();

        gate.Release();
        await Assert.That((await read.Timeout(T(1))).Buffer.Length).IsEqualTo(2);
    }

    /// <summary>
    /// With a tap on, reading and writing through the wrappers allocates nothing per operation once warm.
    /// </summary>
    [Test]
    public async Task Wrappers_SteadyState_AllocateNothing()
    {
        var pipe = new Pipe(InlineOptions);
        var tap = new NoOpTap.Session();
        var writer = new TapPipeWriter(pipe.Writer, tap, null);
        var reader = new TapPipeReader(pipe.Reader, tap, null);

        static void Cycle(TapPipeWriter writer, TapPipeReader reader)
        {
            // Two regions per flush (the second spans a segment boundary), and a read that leaves a remainder.
            writer.GetSpan(3000)[..3000].Fill(1);
            writer.Advance(3000);
            writer.GetSpan(3000)[..3000].Fill(2);
            writer.Advance(3000);
            var flush = writer.FlushAsync();
            flush.GetAwaiter().GetResult();

            var read = reader.ReadAsync();
            var buffer = read.GetAwaiter().GetResult().Buffer;
            reader.AdvanceTo(buffer.GetPosition(buffer.Length - 10), buffer.End);
            read = reader.ReadAsync();
            reader.CancelPendingRead();
            buffer = read.GetAwaiter().GetResult().Buffer;
            reader.AdvanceTo(buffer.End);
        }

        for (var i = 0; i < 100; i++)
            Cycle(writer, reader);

        // The least of a few rounds: a per-operation allocation shows in every round, while the runtime's occasional
        // work on this thread (a pooled async box missing its per-thread cache under load) shows in some.
        var least = long.MaxValue;
        for (var round = 0; round < 5; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                Cycle(writer, reader);
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        await Assert.That(least).IsEqualTo(0);
    }

    // ---- Helpers ----

    // A pool that never trims: the shared array pool drops its arrays after a GC, and re-renting them would be counted
    // against the wrappers.
    private static PipeOptions InlineOptions => new(
        pool: new RetainingPool(), readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline,
        pauseWriterThreshold: 0, useSynchronizationContext: false);

    private sealed class RetainingPool : MemoryPool<byte>
    {
        private readonly Stack<Block> _free = new();

        public override int MaxBufferSize => 4096;

        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            if (minBufferSize > MaxBufferSize)
                throw new ArgumentOutOfRangeException(nameof(minBufferSize));
            return _free.TryPop(out var block) ? block : new Block(this);
        }

        protected override void Dispose(bool disposing)
        {
        }

        private sealed class Block(RetainingPool pool) : IMemoryOwner<byte>
        {
            public Memory<byte> Memory { get; } = new byte[4096];
            public void Dispose() => pool._free.Push(this);
        }
    }

    private async Task<(ISessionTransportView Server, ISessionTransportView Client, INexusTap? Tap)>
        ConnectAndCaptureViews(Type type, INexusTap? tap)
    {
        var serverConfig = CreateServerConfig(type);
        var clientConfig = CreateClientConfig(type);
        serverConfig.Tap = tap;
        clientConfig.Tap = tap;

        var serverView = new TaskCompletionSource<ISessionTransportView>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientView = new TaskCompletionSource<ISessionTransportView>(TaskCreationOptions.RunContinuationsAsynchronously);
        ServerTaps.OnSessionCreated(serverConfig, s => serverView.TrySetResult((ISessionTransportView)s));
        clientConfig.InternalOnSessionSetup = s => clientView.TrySetResult((ISessionTransportView)s);

        var (server, client, _) = CreateServerClient(serverConfig, clientConfig);
        await server.StartAsync().Timeout(T(TestTimeouts.Connect));
        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        return (await serverView.Task.Timeout(T(1)), await clientView.Task.Timeout(T(1)), tap);
    }

    private sealed class RecordingTap : INexusTap
    {
        public ConcurrentQueue<SessionRecorder> Sessions { get; } = new();

        public INexusSessionTap SessionStarted(in NexusTapSessionInfo session)
        {
            var recorder = new SessionRecorder(session);
            Sessions.Enqueue(recorder);
            return recorder;
        }
    }

    private sealed class SessionRecorder(NexusTapSessionInfo info) : INexusSessionTap
    {
        private readonly List<byte> _inbound = new();
        private readonly List<byte> _outbound = new();
        private long _lastTimestamp;

        public NexusTapSessionInfo Info { get; } = info;
        public List<int> ReadSizes { get; } = new();
        public List<int> WriteSizes { get; } = new();
        public long ConnectedId { get; private set; } = -1;
        public DisconnectReason ClosedReason { get; private set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Inbound
        {
            get { lock (_inbound) return _inbound.ToArray(); }
        }

        public byte[] Outbound
        {
            get { lock (_outbound) return _outbound.ToArray(); }
        }

        public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp)
        {
            if (data.IsEmpty)
                throw new InvalidOperationException("Empty read reported.");
            lock (_inbound)
            {
                _inbound.AddRange(data.ToArray());
                ReadSizes.Add((int)data.Length);
            }

            Volatile.Write(ref _lastTimestamp, timestamp);
            return default;
        }

        public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp)
        {
            if (data.IsEmpty)
                throw new InvalidOperationException("Empty write reported.");
            lock (_outbound)
            {
                _outbound.AddRange(data.ToArray());
                WriteSizes.Add((int)data.Length);
            }

            return default;
        }

        public void OnConnected(long sessionId) => ConnectedId = sessionId;

        public void OnClosed(DisconnectReason reason)
        {
            ClosedReason = reason;
            if (!Closed.TrySetResult())
                throw new InvalidOperationException("Closed twice.");
        }
    }

    private sealed class NoOpTap : INexusTap
    {
        public INexusSessionTap SessionStarted(in NexusTapSessionInfo session) => new Session();

        public sealed class Session : INexusSessionTap
        {
            public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp) => default;
            public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp) => default;
            public void OnConnected(long sessionId) { }
            public void OnClosed(DisconnectReason reason) { }
        }
    }

    private sealed class DecliningTap : INexusTap
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public INexusSessionTap? SessionStarted(in NexusTapSessionInfo session)
        {
            Interlocked.Increment(ref _calls);
            return null;
        }
    }

    private sealed class ThrowingTap : INexusTap
    {
        public INexusSessionTap SessionStarted(in NexusTapSessionInfo session) => new Session();

        private sealed class Session : INexusSessionTap
        {
            public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp) => throw new InvalidOperationException("read");
            public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp) => ValueTask.FromException(new InvalidOperationException("write"));
            public void OnConnected(long sessionId) => throw new InvalidOperationException("connected");
            public void OnClosed(DisconnectReason reason) => throw new InvalidOperationException("closed");
        }
    }

    private sealed class BlockingTap : INexusSessionTap
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _gate.TrySetResult();
        public ValueTask OnRead(ReadOnlySequence<byte> data, long timestamp) => new(_gate.Task);
        public ValueTask OnWrite(ReadOnlySequence<byte> data, long timestamp) => new(_gate.Task);
        public void OnConnected(long sessionId) { }
        public void OnClosed(DisconnectReason reason) { }
    }
}
