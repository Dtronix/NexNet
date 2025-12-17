using System.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

public class NexusPipeWriterTests
{
    [Test]
    public void Advance_And_GetMemory_Span_DoNotThrow()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 1024);

        // Should get a non-empty Memory and Span
        var mem = writer.GetMemory(10);
        Assert.That(mem.Length, Is.GreaterThanOrEqualTo(10));

        var span = writer.GetSpan(20);
        Assert.That(span.Length, Is.GreaterThanOrEqualTo(20));

        // Advance should not throw
        Assert.DoesNotThrow(() => writer.Advance(15));
    }

    [Test]
    public void Complete_Throws_InvalidOperationException()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: true, chunkSize: 128);

        Assert.Throws<InvalidOperationException>(() => writer.Complete());
    }


    [TestCase(true)]
    [TestCase(false)]
    public async Task CompleteAsync_OnlyNotifies_WhenUpdateStateTrue(bool updateStateReturns)
    {
        var state = new SpyStateManager { UpdateStateReturnValue = updateStateReturns };
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 64);

        await writer.CompleteAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.LastUpdatedState,
                Is.EqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete));
            Assert.That(state.NotifyStateCalled, Is.EqualTo(updateStateReturns));
        }
    }


    [Test]
    public async Task FlushAsync_EmptyBuffer_ReturnsImmediately()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 64);

        var result = await writer.FlushAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsCanceled, Is.False);
            Assert.That(result.IsCompleted, Is.False);
            Assert.That(messenger.SendHeaderWithBodyCount, Is.Zero);
        }
    }


    [Test]
    public async Task FlushAsync_SingleChunk_SendsExactlyOnce()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 100);

        // Write 30 bytes
        var span = writer.GetSpan(30);
        for (int i = 0; i < 30; i++) span[i] = (byte)i;
        writer.Advance(30);

        var result = await writer.FlushAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsCanceled, Is.False);
            Assert.That(result.IsCompleted, Is.False);

            Assert.That(messenger.SendHeaderWithBodyCount, Is.EqualTo(1));
            Assert.That(messenger.BodyLengths.Single(), Is.EqualTo(30));
        }
    }


    [Test]
    public async Task FlushAsync_MultiChunk_SendsMultipleTimes()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        // force chunking at 5 bytes
        var writer = new NexusPipeWriter(state, null, messenger, isServer: true, chunkSize: 5);

        // Write 12 bytes so we get 3 flushes: 5 + 5 + 2
        var span = writer.GetSpan(12);
        for (int i = 0; i < 12; i++) span[i] = (byte)i;
        writer.Advance(12);

        var result = await writer.FlushAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsCanceled, Is.False);
            Assert.That(result.IsCompleted, Is.False);

            Assert.That(messenger.SendHeaderWithBodyCount, Is.EqualTo(3));
            Assert.That(messenger.BodyLengths, Is.EqualTo(new[] { 5, 5, 2 }).AsCollection);
        }
    }


    [Test]
    public async Task CancelPendingFlush_BeforeFlush_ResultsInCanceledFlushResult()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 100);

        // Write something so buffer is non-empty
        var span = writer.GetSpan(10);
        writer.Advance(10);

        // Cancel before flushing
        writer.CancelPendingFlush();

        var result = await writer.FlushAsync();
        Assert.That(result.IsCanceled, Is.True);
        Assert.That(result.IsCompleted, Is.False);
        // No send should occur once canceled
        Assert.That(messenger.SendHeaderWithBodyCount, Is.Zero);
    }

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 64);
        Assert.DoesNotThrow(() => writer.Dispose());
    }

    [Test]
    public async Task FlushAsync_OnInvalidOperationException_SetsCompletedAndNotifiesState()
    {
        var state = new SpyStateManager();
        var messenger = new ThrowingMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 10);

        // Write a few bytes so buffer isn't empty
        var span = writer.GetSpan(5);
        writer.Advance(5);

        var result = await writer.FlushAsync();

        // exception should mark complete and notify state
        Assert.That(result.IsCompleted, Is.True);
        Assert.That(state.LastUpdatedState,
            Is.EqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete));
        Assert.That(state.NotifyStateCalled, Is.True);
    }

    [Test]
    public async Task FlushAsync_OnTaskCanceledException_StopsWithoutCompleting()
    {
        var state = new SpyStateManager();
        var messenger = new CancelingMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: true, chunkSize: 10);

        // Write data
        var span = writer.GetSpan(8);
        writer.Advance(8);

        var result = await writer.FlushAsync();

        // TaskCanceledException should break the loop but not mark complete
        Assert.That(result.IsCanceled, Is.False, "Cancellation from messenger isn't tracked as _isCanceled");
        Assert.That(result.IsCompleted, Is.False);
        // It should have attempted exactly one send before throwing
        // (we can't count here, but we know no NotifyState was called)
        Assert.That(state.NotifyStateCalled, Is.False);
    }

    [Test]
    public async Task FlushAsync_WhenPausedAndTokenAlreadyCancelled_ReturnsCanceledWithoutSending()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 10);

        // Make buffer non-empty
        var span = writer.GetSpan(4);
        writer.Advance(4);

        // Pause writing so FlushAsync will wait on the semaphore
        writer.PauseWriting = true;

        // Provide a cancellation token that's already canceled
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await writer.FlushAsync(cts.Token);

        Assert.That(result.IsCanceled, Is.True, "Should report canceled when token is already cancelled");
        Assert.That(result.IsCompleted, Is.False);
        Assert.That(messenger.SendHeaderWithBodyCount, Is.Zero, "Should not send any data when immediately cancelled");
    }

    [Test]
    public async Task FlushAsync_Blocks_WhenPaused_Then_Resumes_WhenUnpaused()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 100);

        // Write 50 bytes so buffer isn't empty
        var mem = writer.GetSpan(50);
        for (int i = 0; i < 50; i++) mem[i] = (byte)i;
        writer.Advance(50);

        // Pause before flushing
        writer.PauseWriting = true;

        // Start flush but it should block on the semaphore
        var flushTask = writer.FlushAsync();

        // Give it a moment—no SendHeaderWithBody calls yet
        await Task.Delay(50);
        Assert.That(messenger.SendHeaderWithBodyCount, Is.Zero, "Should not send while paused");

        // Now resume
        writer.PauseWriting = false;

        // Await completion and verify it sent exactly once
        var result = await flushTask;
        Assert.That(result.IsCanceled, Is.False);
        Assert.That(result.IsCompleted, Is.False);
        Assert.That(messenger.SendHeaderWithBodyCount, Is.EqualTo(1));
    }

    // A minimal spy for ISessionMessenger
    class SpySessionMessenger : ISessionMessenger
    {
        public int SendHeaderWithBodyCount { get; private set; }
        public int[] BodyLengths => _bodyLengths.ToArray();
        private readonly List<int> _bodyLengths = new();

        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
            where TMessage : IMessageBase => throw new NotImplementedException();

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask SendHeaderWithBody(MessageType type,
            ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask SendHeaderWithBody(MessageType type,
            ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
        {
            SendHeaderWithBodyCount++;
            _bodyLengths.Add((int)body.Length);
            return default;
        }

        public Task DisconnectAsync(DisconnectReason reason, 
            [CallerFilePath]string? filePath = null, 
            [CallerLineNumber] int? lineNumber = null) => Task.CompletedTask;
    }

    // A messenger that always throws InvalidOperationException in SendHeaderWithBody
    private class ThrowingMessenger : ISessionMessenger
    {
        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
            where TMessage : IMessageBase => default;

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default) => default;

        public ValueTask SendHeaderWithBody(MessageType type,
            ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
            => default;

        public ValueTask SendHeaderWithBody(MessageType type,
            ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated failure");

        public Task DisconnectAsync(DisconnectReason reason, 
            [CallerFilePath]string? filePath = null, 
            [CallerLineNumber] int? lineNumber = null) => Task.CompletedTask;
    }

    // A messenger that always throws TaskCanceledException in SendHeaderWithBody
    private class CancelingMessenger : ISessionMessenger
    {
        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
            where TMessage : IMessageBase => default;

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default) => default;

        public ValueTask SendHeaderWithBody(MessageType type,
            ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
            => default;

        public ValueTask SendHeaderWithBody(MessageType type,
            ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
            => throw new TaskCanceledException("simulated cancel");

        public Task DisconnectAsync(DisconnectReason reason, 
            [CallerFilePath]string? filePath = null, 
            [CallerLineNumber] int? lineNumber = null) => Task.CompletedTask;
    }
    
    
    // A minimal spy for IPipeStateManager
    class SpyStateManager : IPipeStateManager
    {
        public ushort Id { get; } = 1;
        public bool NotifyStateCalled { get; private set; }
        public NexusDuplexPipe.State LastUpdatedState { get; private set; }
        public bool LastRemoveFlag { get; private set; }
        public bool UpdateStateReturnValue { get; set; } = true;


        public ValueTask NotifyState()
        {
            NotifyStateCalled = true;
            return default;
        }

        public bool UpdateState(NexusDuplexPipe.State updatedState, bool remove = false)
        {
            LastUpdatedState = updatedState;
            LastRemoveFlag = remove;
            return UpdateStateReturnValue;
        }

        public NexusDuplexPipe.State CurrentState => LastUpdatedState;
    }



}
