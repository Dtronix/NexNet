using System.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// A pipe disposed (released or reset) while its writer is still flushing must end only that pipe: the flush never
/// reads released buffers, and the session is never disconnected for it.
/// </summary>
public class NexusPipeWriterCloseRaceTests
{
    [Test]
    public async Task Dispose_WhileAChunkIsBeingSent_KeepsTheBufferUntilTheSendEnds()
    {
        var state = new StateManager();
        var messenger = new GatedMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: true, chunkSize: 1024);
        writer.AddCredit(1 << 20);

        const int length = 10 * 1024;
        var span = writer.GetSpan(length);
        for (var i = 0; i < length; i++)
            span[i] = (byte)i;
        writer.Advance(length);

        var flush = writer.FlushAsync().AsTask();
        await messenger.Entered.Task.Timeout(1);

        // The pipe closes while the first chunk is being sent.
        writer.Dispose();
        messenger.Gate.TrySetResult();

        var result = await flush.Timeout(1);
        using (Assert.Multiple())
        {
            await Assert.That(messenger.Errors).IsEmpty();
            await Assert.That(messenger.Disconnects).IsEmpty();
            await Assert.That(result.IsCompleted).IsTrue();

            // The chunk in flight is sent intact; nothing follows it.
            await Assert.That(messenger.Bodies.Count).IsEqualTo(1);
            await Assert.That(messenger.Bodies[0]).IsEquivalentTo(
                Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray(), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task Dispose_BeforeAFlush_CompletesTheFlushWithoutSending()
    {
        var messenger = new GatedMessenger();
        messenger.Gate.TrySetResult();
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: true, chunkSize: 1024);
        writer.AddCredit(1 << 20);
        writer.GetSpan(100);
        writer.Advance(100);

        writer.Dispose();
        var result = await writer.FlushAsync().AsTask().Timeout(1);

        using (Assert.Multiple())
        {
            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(messenger.Bodies).IsEmpty();
            await Assert.That(messenger.Disconnects).IsEmpty();
        }
    }

    [Test]
    public async Task Dispose_WhileWaitingForCredit_EndsTheFlush()
    {
        var messenger = new GatedMessenger();
        messenger.Gate.TrySetResult();
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: true, chunkSize: 1024);
        writer.GetSpan(100);
        writer.Advance(100);

        // No credit: the flush waits.
        var flush = writer.FlushAsync().AsTask();
        await Task.Delay(20);
        writer.Dispose();

        var result = await flush.Timeout(1);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(messenger.Bodies).IsEmpty();
            await Assert.That(messenger.Disconnects).IsEmpty();
        }
    }

    [Test]
    public async Task Dispose_RacingFlushes_NeverTouchesReleasedBuffers()
    {
        // Stress: dispose at a random point of a multi-chunk flush, many times.
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var messenger = new YieldingMessenger();
            var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: iteration % 2 == 0, chunkSize: 4096);
            writer.AddCredit(1 << 20);

            const int length = 200 * 1024;
            var span = writer.GetSpan(length);
            for (var i = 0; i < length; i++)
                span[i] = (byte)(i % 251);
            writer.Advance(length);

            var flush = Task.Run(async () => await writer.FlushAsync());
            var delay = Random.Shared.Next(0, 200);
            var dispose = Task.Run(() =>
            {
                Thread.SpinWait(delay * 50);
                writer.Dispose();
            });

            await Task.WhenAll(flush, dispose).Timeout(5);

            if (messenger.Errors.Count > 0 || messenger.Disconnects.Count > 0 || !messenger.DataIntact)
            {
                Assert.Fail($"Iteration {iteration}: errors {string.Join("; ", messenger.Errors)}, " +
                            $"disconnects {string.Join(", ", messenger.Disconnects)}, intact {messenger.DataIntact}.");
            }
        }
    }

    [Test]
    public async Task Dispose_WhileTheApplicationHoldsASpan_WritesAreDroppedSafely()
    {
        var messenger = new GatedMessenger();
        messenger.Gate.TrySetResult();
        var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: false, chunkSize: 1024);
        writer.AddCredit(1 << 20);

        var memory = writer.GetMemory(100);
        writer.Dispose();

        // The buffer is still the application's until Advance.
        memory.Span.Slice(0, 100).Fill(7);
        writer.Advance(100);

        // Later writes go nowhere.
        writer.GetSpan(5000).Slice(0, 5000).Fill(1);
        writer.Advance(5000);
        var result = await writer.FlushAsync().AsTask().Timeout(1);

        using (Assert.Multiple())
        {
            await Assert.That(result.IsCompleted).IsTrue();
            await Assert.That(messenger.Bodies).IsEmpty();
            await Assert.That(messenger.Disconnects).IsEmpty();
        }
    }

    [Test]
    public async Task Dispose_RacingWrites_NeverThrows()
    {
        // Stress: the application keeps writing and flushing while the pipe is disposed on another thread.
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var messenger = new YieldingMessenger();
            var writer = new NexusPipeWriter(new StateManager(), null, messenger, isServer: true, chunkSize: 4096);
            writer.AddCredit(1 << 30);

            var data = new byte[251 * 80];
            for (var i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 251);

            Exception? error = null;
            var writing = Task.Run(async () =>
            {
                try
                {
                    for (var i = 0; i < 50; i++)
                    {
                        if ((await writer.WriteAsync(data)).IsCompleted)
                            break;
                    }
                }
                catch (Exception e)
                {
                    error = e;
                }
            });
            var delay = Random.Shared.Next(0, 400);
            var dispose = Task.Run(() =>
            {
                Thread.SpinWait(delay * 50);
                writer.Dispose();
            });

            await Task.WhenAll(writing, dispose).Timeout(5);

            if (error != null || messenger.Errors.Count > 0 || messenger.Disconnects.Count > 0)
            {
                Assert.Fail($"Iteration {iteration}: {error}; errors {string.Join("; ", messenger.Errors)}, " +
                            $"disconnects {string.Join(", ", messenger.Disconnects)}.");
            }
        }
    }

    private abstract class MessengerBase : ISessionMessenger
    {
        public List<string> Errors { get; } = new();
        public List<DisconnectReason> Disconnects { get; } = new();

        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
            where TMessage : IMessageBase => default;

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default) => default;

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default) => default;

        public abstract ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body, CancellationToken cancellationToken = default);

        public Task DisconnectAsync(DisconnectReason reason,
            [CallerFilePath] string? filePath = null,
            [CallerLineNumber] int? lineNumber = null)
        {
            lock (Disconnects)
                Disconnects.Add(reason);
            return Task.CompletedTask;
        }

        // Reads the body as the session does when it copies the frame to the transport.
        protected byte[]? Read(ReadOnlySequence<byte> body)
        {
            try
            {
                return body.ToArray();
            }
            catch (Exception e)
            {
                lock (Errors)
                    Errors.Add(e.GetType().Name + ": " + e.Message);
                throw;
            }
        }
    }

    // Holds the first send until Gate is set.
    private sealed class GatedMessenger : MessengerBase
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Bodies { get; } = new();

        public override async ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Gate.Task.ConfigureAwait(false);
            Bodies.Add(Read(body)!);
        }
    }

    // Yields before reading each chunk, like a send waiting for the write lock, and checks the bytes.
    private sealed class YieldingMessenger : MessengerBase
    {
        private long _offset;
        public bool DataIntact { get; private set; } = true;

        public override async ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var bytes = Read(body)!;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)((_offset + i) % 251))
                {
                    DataIntact = false;
                    break;
                }
            }

            _offset += bytes.Length;
        }
    }

    private sealed class StateManager : IPipeStateManager
    {
        public ushort Id => 1;
        public NexusDuplexPipe.State CurrentState { get; private set; }

        public ValueTask NotifyState() => default;

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            if ((CurrentState & updatedState) == updatedState)
                return false;
            CurrentState |= updatedState;
            return true;
        }
    }
}
