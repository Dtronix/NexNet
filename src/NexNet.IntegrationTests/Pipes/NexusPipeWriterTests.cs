using System.Buffers;
using System.Runtime.CompilerServices;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pipes;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Pipes;

public class NexusPipeWriterTests
{
    [Test]
    public async Task Advance_And_GetMemory_Span_DoNotThrow()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 1024).WithCredit();

        // Should get a non-empty Memory and Span
        var mem = writer.GetMemory(10);
        await Assert.That(mem.Length).IsGreaterThanOrEqualTo(10);

        var span = writer.GetSpan(20);
        await Assert.That(span.Length).IsGreaterThanOrEqualTo(20);

        // Advance should not throw
        await Assert.That(() => writer.Advance(15)).ThrowsNothing();
    }

    [Test]
    public async Task Complete_Throws_InvalidOperationException()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: true, chunkSize: 128).WithCredit();

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.Complete());
    }


    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CompleteAsync_OnlyNotifies_WhenUpdateStateTrue(bool updateStateReturns)
    {
        var state = new SpyStateManager { UpdateStateReturnValue = updateStateReturns };
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 64).WithCredit();

        await writer.CompleteAsync();

        using (Assert.Multiple())
        {
            await Assert.That(state.LastUpdatedState).IsEqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete);
            await Assert.That(state.NotifyStateCalled).IsEqualTo(updateStateReturns);
        }
    }


    [Test]
    public async Task FlushAsync_EmptyBuffer_ReturnsImmediately()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 64).WithCredit();

        var result = await writer.FlushAsync();
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCanceled).IsFalse();
            await Assert.That(result.IsCompleted).IsFalse();
            await Assert.That(messenger.SendHeaderWithBodyCount).IsZero();
        }
    }


    [Test]
    public async Task FlushAsync_SingleChunk_SendsExactlyOnce()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 100).WithCredit();

        // Write 30 bytes
        var span = writer.GetSpan(30);
        for (int i = 0; i < 30; i++) span[i] = (byte)i;
        writer.Advance(30);

        var result = await writer.FlushAsync();
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCanceled).IsFalse();
            await Assert.That(result.IsCompleted).IsFalse();

            await Assert.That(messenger.SendHeaderWithBodyCount).IsEqualTo(1);
            await Assert.That(messenger.BodyLengths.Single()).IsEqualTo(30);
        }
    }


    [Test]
    public async Task FlushAsync_MultiChunk_SendsMultipleTimes()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        // force chunking at 5 bytes
        var writer = new NexusPipeWriter(state, null, messenger, isServer: true, chunkSize: 5).WithCredit();

        // Write 12 bytes so we get 3 flushes: 5 + 5 + 2
        var span = writer.GetSpan(12);
        for (int i = 0; i < 12; i++) span[i] = (byte)i;
        writer.Advance(12);

        var result = await writer.FlushAsync();
        using (Assert.Multiple())
        {
            await Assert.That(result.IsCanceled).IsFalse();
            await Assert.That(result.IsCompleted).IsFalse();

            await Assert.That(messenger.SendHeaderWithBodyCount).IsEqualTo(3);
            await Assert.That(messenger.BodyLengths).IsEquivalentTo(new[] { 5, 5, 2 }, CollectionOrdering.Matching);
        }
    }


    [Test]
    public async Task CancelPendingFlush_BeforeFlush_ResultsInCanceledFlushResult()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 100).WithCredit();

        // Write something so buffer is non-empty
        var span = writer.GetSpan(10);
        writer.Advance(10);

        // Cancel before flushing
        writer.CancelPendingFlush();

        var result = await writer.FlushAsync();
        await Assert.That(result.IsCanceled).IsTrue();
        await Assert.That(result.IsCompleted).IsFalse();
        // No send should occur once canceled
        await Assert.That(messenger.SendHeaderWithBodyCount).IsZero();
    }

    [Test]
    public async Task Dispose_DoesNotThrow()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 64).WithCredit();
        await Assert.That(() => writer.Dispose()).ThrowsNothing();
    }

    [Test]
    public async Task FlushAsync_OnInvalidOperationException_SetsCompletedAndNotifiesState()
    {
        var state = new SpyStateManager();
        var messenger = new ThrowingMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 10).WithCredit();

        // Write a few bytes so buffer isn't empty
        var span = writer.GetSpan(5);
        writer.Advance(5);

        var result = await writer.FlushAsync();

        // exception should mark complete and notify state
        await Assert.That(result.IsCompleted).IsTrue();
        await Assert.That(state.LastUpdatedState).IsEqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete);
        await Assert.That(state.NotifyStateCalled).IsTrue();
    }

    [Test]
    public async Task FlushAsync_OnTaskCanceledException_StopsWithoutCompleting()
    {
        var state = new SpyStateManager();
        var messenger = new CancelingMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: true, chunkSize: 10).WithCredit();

        // Write data
        var span = writer.GetSpan(8);
        writer.Advance(8);

        var result = await writer.FlushAsync();

        // TaskCanceledException should break the loop but not mark complete
        await Assert.That(result.IsCanceled).IsFalse().Because("Cancellation from messenger isn't tracked as _isCanceled");
        await Assert.That(result.IsCompleted).IsFalse();
        // It should have attempted exactly one send before throwing
        // (we can't count here, but we know no NotifyState was called)
        await Assert.That(state.NotifyStateCalled).IsFalse();
    }

    [Test]
    public async Task FlushAsync_WithoutCreditAndTokenAlreadyCancelled_ReturnsCanceledWithoutSending()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 10);

        // Make buffer non-empty
        var span = writer.GetSpan(4);
        writer.Advance(4);

        // No credit, so FlushAsync would wait for a grant.
        // Provide a cancellation token that's already canceled
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await writer.FlushAsync(cts.Token);

        await Assert.That(result.IsCanceled).IsTrue().Because("Should report canceled when token is already cancelled");
        await Assert.That(result.IsCompleted).IsFalse();
        await Assert.That(messenger.SendHeaderWithBodyCount).IsZero().Because("Should not send any data when immediately cancelled");
    }

    [Test]
    public async Task FlushAsync_Blocks_WithoutCredit_Then_Resumes_WhenCreditIsGranted()
    {
        var state = new SpyStateManager();
        var messenger = new SpySessionMessenger();
        var writer = new NexusPipeWriter(state, null, messenger, isServer: false, chunkSize: 100);

        // Write 50 bytes so buffer isn't empty
        var mem = writer.GetSpan(50);
        for (int i = 0; i < 50; i++) mem[i] = (byte)i;
        writer.Advance(50);

        // No credit yet: the flush waits for a grant.
        var flushTask = writer.FlushAsync();

        // Give it a moment—no SendHeaderWithBody calls yet
        await Task.Delay(50);
        await Assert.That(messenger.SendHeaderWithBodyCount).IsZero().Because("Should not send without credit");

        // Now grant the peer's window
        writer.AddCredit(50);

        // Await completion and verify it sent exactly once
        var result = await flushTask;
        await Assert.That(result.IsCanceled).IsFalse();
        await Assert.That(result.IsCompleted).IsFalse();
        await Assert.That(messenger.SendHeaderWithBodyCount).IsEqualTo(1);
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