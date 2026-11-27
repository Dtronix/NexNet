using System.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Transports;
using NexNet.Transports.Uds;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// A canceled flush leaves what it did not send buffered (the PipeWriter contract): the next flush sends it, so the
/// reader gets every byte exactly once and in order. Dropping it tore the stream at the window boundary (I09b).
/// </summary>
public class NexusPipeWriterCancellationTests
{
    [Test]
    public async Task FlushCanceledWhileWaitingForCredit_TheNextFlushSendsTheRest()
    {
        var messenger = new RecordingMessenger();
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: false, chunkSize: 4).WithCredit(10);
        var data = Pattern(25);
        writer.Write(data);

        using var cts = new CancellationTokenSource();
        var flush = writer.FlushAsync(cts.Token).AsTask();
        await WaitForCreditWaitAsync(writer);
        await Assert.That(messenger.Sent.Count).IsEqualTo(10);

        cts.Cancel();
        var canceled = await flush.Timeout(1);
        await Assert.That(canceled.IsCanceled).IsTrue();

        writer.AddCredit(100);
        var result = await writer.FlushAsync().AsTask().Timeout(1);

        using (Assert.Multiple())
        {
            await Assert.That(result.IsCanceled).IsFalse();
            await Assert.That(messenger.Sent).IsEquivalentTo(data, CollectionOrdering.Matching);
            await Assert.That(writer.Credit).IsEqualTo(100 - 15);
        }
    }

    [Test]
    public async Task CancelPendingFlush_WhileWaitingForCredit_TheNextFlushSendsTheRest()
    {
        var messenger = new RecordingMessenger();
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: false, chunkSize: 4).WithCredit(10);
        var data = Pattern(25);
        writer.Write(data);

        var flush = writer.FlushAsync().AsTask();
        await WaitForCreditWaitAsync(writer);

        writer.CancelPendingFlush();
        await Assert.That((await flush.Timeout(1)).IsCanceled).IsTrue();

        // CancelPendingFlush cancels one flush; the next one runs.
        writer.AddCredit(100);
        var result = await writer.FlushAsync().AsTask().Timeout(1);

        using (Assert.Multiple())
        {
            await Assert.That(result.IsCanceled).IsFalse();
            await Assert.That(messenger.Sent).IsEquivalentTo(data, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task CancelPendingFlush_BeforeAFlush_CancelsOnlyThatFlush()
    {
        var messenger = new RecordingMessenger();
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: false, chunkSize: 4).WithCredit();
        var data = Pattern(10);
        writer.Write(data);

        writer.CancelPendingFlush();
        var canceled = await writer.FlushAsync().AsTask().Timeout(1);
        await Assert.That(canceled.IsCanceled).IsTrue();
        await Assert.That(messenger.Sent).IsEmpty();

        var result = await writer.FlushAsync().AsTask().Timeout(1);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCanceled).IsFalse();
            await Assert.That(messenger.Sent).IsEquivalentTo(data, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task FlushCanceledWhileAChunkWaitsForTheSession_KeepsTheChunkAndItsCredit()
    {
        // The session throws for a chunk whose token was canceled before it was written, as it does while waiting
        // for its write lock.
        using var cts = new CancellationTokenSource();
        var messenger = new RecordingMessenger { CancelAfterFirstSend = cts };
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: false, chunkSize: 10).WithCredit(1000);
        var data = Pattern(30);
        writer.Write(data);

        var canceled = await writer.FlushAsync(cts.Token).AsTask().Timeout(1);
        using (Assert.Multiple())
        {
            await Assert.That(canceled.IsCanceled).IsTrue();
            await Assert.That(messenger.Sent.Count).IsEqualTo(10);
            await Assert.That(writer.Credit).IsEqualTo(1000 - 10);
        }

        var result = await writer.FlushAsync().AsTask().Timeout(1);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCanceled).IsFalse();
            await Assert.That(messenger.Sent).IsEquivalentTo(data, CollectionOrdering.Matching);
            await Assert.That(writer.Credit).IsEqualTo(1000 - 30);
        }
    }

    [Test]
    public async Task FlushCanceledAcrossSegments_ReleasesOnlyWhatWasSent()
    {
        // Three 64 KiB segments; the credit ends inside the second one.
        var pool = new RecordingArrayPool();
        var messenger = new RecordingMessenger();
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: false,
            chunkSize: NexusPipeWriter.MaxChunkSize, bufferPool: pool).WithCredit(100_000);
        const int segment = 64 * 1024;
        var data = Pattern(3 * segment);
        for (var i = 0; i < 3; i++)
        {
            data.AsSpan(i * segment, segment).CopyTo(writer.GetSpan(segment));
            writer.Advance(segment);
        }

        using var cts = new CancellationTokenSource();
        var flush = writer.FlushAsync(cts.Token).AsTask();
        await WaitForCreditWaitAsync(writer);
        cts.Cancel();
        await Assert.That((await flush.Timeout(1)).IsCanceled).IsTrue();

        using (Assert.Multiple())
        {
            await Assert.That(messenger.Sent.Count).IsEqualTo(100_000);
            await Assert.That(pool.Rented.Count).IsEqualTo(3);
            await Assert.That(pool.Returned.Count).IsEqualTo(1);
        }

        writer.AddCredit(data.Length);
        await writer.FlushAsync().AsTask().Timeout(1);
        using (Assert.Multiple())
        {
            await Assert.That(messenger.Sent).IsEquivalentTo(data, CollectionOrdering.Matching);
            await Assert.That(pool.Returned.Count).IsEqualTo(2);
        }

        writer.Dispose();
        await Assert.That(pool.Returned).IsEquivalentTo(pool.Rented);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletedAfterACanceledFlush_ReleasesItsBuffers(bool cancelPendingFlush)
    {
        var pool = new RecordingArrayPool();
        var messenger = new RecordingMessenger();
        var state = new StateManager();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 1024, bufferPool: pool)
            .WithCredit(100);
        writer.Write(Pattern(5000));

        using var cts = new CancellationTokenSource();
        var flush = writer.FlushAsync(cancelPendingFlush ? CancellationToken.None : cts.Token).AsTask();
        await WaitForCreditWaitAsync(writer);
        if (cancelPendingFlush)
            writer.CancelPendingFlush();
        else
            cts.Cancel();
        await Assert.That((await flush.Timeout(1)).IsCanceled).IsTrue();

        // The unsent remainder is still held.
        await Assert.That(pool.Returned).IsEmpty();

        await writer.CompleteAsync();
        await Assert.That(state.LastUpdatedState).IsEqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete);

        // Completed: a flush sends nothing more.
        writer.AddCredit(10_000);
        var after = await writer.FlushAsync().AsTask().Timeout(1);
        await Assert.That(after.IsCompleted).IsTrue();
        await Assert.That(messenger.Sent.Count).IsEqualTo(100);

        // The pipe is freed: every buffer goes back to the pool.
        writer.Dispose();
        await Assert.That(pool.Returned).IsEquivalentTo(pool.Rented);
    }

    [Test]
    public async Task CompletedWhileAFlushWaitsForCredit_DropsTheRestAndReleasesEverything()
    {
        var pool = new RecordingArrayPool();
        var writer = new NexusPipeWriter(new StateManager(), null, new RecordingMessenger(), isServer: false,
            chunkSize: 1024, bufferPool: pool).WithCredit(100);
        writer.Write(Pattern(5000));

        var flush = writer.FlushAsync().AsTask();
        await WaitForCreditWaitAsync(writer);

        // The pipe closes from the other side, then is freed.
        writer.SetComplete();
        var result = await flush.Timeout(1);
        writer.Dispose();

        using (Assert.Multiple())
        {
            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(writer.Credit).IsEqualTo(0L);
            await Assert.That(pool.Returned).IsEquivalentTo(pool.Rented);
        }
    }

    /// <summary>
    /// Over a real session with the smallest window: a flush canceled while blocked on the window, then flushed
    /// again, delivers every byte exactly once and in order.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OverASession_FlushCanceledOnTheWindow_ThenFlushedAgain_DeliversEveryByteOnce(bool cancelPendingFlush)
    {
        const int window = 4 * 1024;
        const int total = 64 * 1024;
        var path = Path.Combine(Path.GetTempPath(), "nxcancel_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var endPoint = new System.Net.Sockets.UnixDomainSocketEndPoint(path);
        var serverPipe = new TaskCompletionSource<INexusDuplexPipe>(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new NexusServer<ServerNexus, ServerNexus.ClientProxy>(
            new UdsServerConfig { EndPoint = endPoint, PipeWindow = window },
            () => new ServerNexus
            {
                ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
                {
                    serverPipe.TrySetResult(pipe);
                    await done.Task.ConfigureAwait(false);
                }
            });
        await server.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var client = new NexusClient<ClientNexus, ClientNexus.ServerProxy>(new UdsClientConfig { EndPoint = endPoint }, new ClientNexus());
        try
        {
            await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var pipe = client.CreatePipe();
            await client.Proxy.ServerTaskValueWithDuplexPipe(pipe);
            await pipe.ReadyTask.WaitAsync(TimeSpan.FromSeconds(5));
            var reader = (await serverPipe.Task.WaitAsync(TimeSpan.FromSeconds(5))).Input;

            var writer = (NexusPipeWriter)pipe.Output;
            var data = Pattern(total);
            writer.Write(data);

            // The server does not read yet, so the flush stops at the window.
            using var cts = new CancellationTokenSource();
            var flush = writer.FlushAsync(cancelPendingFlush ? CancellationToken.None : cts.Token).AsTask();
            await WaitForCreditWaitAsync(writer);
            if (cancelPendingFlush)
                writer.CancelPendingFlush();
            else
                cts.Cancel();
            await Assert.That((await flush.WaitAsync(TimeSpan.FromSeconds(5))).IsCanceled).IsTrue();

            var reading = Task.Run(async () =>
            {
                var received = new List<byte>();
                while (true)
                {
                    var r = await reader.ReadAsync();
                    foreach (var segment in r.Buffer)
                        received.AddRange(segment.ToArray());
                    reader.AdvanceTo(r.Buffer.End);
                    if (r.IsCompleted)
                        return received.ToArray();
                }
            });

            var again = await writer.FlushAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(again.IsCanceled).IsFalse();
            await writer.CompleteAsync();

            var all = await reading.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(all).IsEquivalentTo(data, CollectionOrdering.Matching);
        }
        finally
        {
            done.TrySetResult();
            await client.DisconnectAsync();
            await server.StopAsync();
        }
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)(i * 7 + i / 251);
        return data;
    }

    private static async Task WaitForCreditWaitAsync(NexusPipeWriter writer)
    {
        for (var i = 0; i < 500 && writer.CreditWait.WaitSince == 0; i++)
            await Task.Delay(10);
        await Assert.That(writer.CreditWait.WaitSince).IsNotEqualTo(0L).Because("The flush never waited for credit.");
    }

    /// <summary>
    /// Records the bytes of every chunk sent. Like the session, it throws for a chunk whose token is canceled.
    /// </summary>
    private sealed class RecordingMessenger : ISessionMessenger
    {
        private readonly object _lock = new();
        private readonly List<byte> _sent = new();

        /// <summary>Canceled once the first chunk is sent.</summary>
        public CancellationTokenSource? CancelAfterFirstSend { get; init; }

        public List<byte> Sent
        {
            get
            {
                lock (_lock)
                    return new List<byte>(_sent);
            }
        }

        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
            where TMessage : IMessageBase => default;

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default) => default;

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default) => default;

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromException(new TaskCanceledException(null, null, cancellationToken));

            lock (_lock)
                _sent.AddRange(body.ToArray());
            CancelAfterFirstSend?.Cancel();
            return default;
        }

        public Task DisconnectAsync(DisconnectReason reason,
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int? lineNumber = null) => Task.CompletedTask;
    }

    /// <summary>
    /// Records what is rented and returned.
    /// </summary>
    private sealed class RecordingArrayPool : ArrayPool<byte>
    {
        private readonly object _lock = new();
        private readonly List<byte[]> _rented = new();
        private readonly List<byte[]> _returned = new();

        public List<byte[]> Rented { get { lock (_lock) return new List<byte[]>(_rented); } }
        public List<byte[]> Returned { get { lock (_lock) return new List<byte[]>(_returned); } }

        public override byte[] Rent(int minimumLength)
        {
            var array = new byte[minimumLength];
            lock (_lock)
                _rented.Add(array);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_lock)
                _returned.Add(array);
        }
    }

    private sealed class StateManager : IPipeStateManager
    {
        public ushort Id => 1;
        public NexusDuplexPipe.State LastUpdatedState { get; private set; }
        public NexusDuplexPipe.State CurrentState => LastUpdatedState;
        public ValueTask NotifyState() => default;

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            LastUpdatedState = updatedState;
            return true;
        }
    }
}
