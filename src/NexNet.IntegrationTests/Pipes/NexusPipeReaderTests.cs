using System.Buffers;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

public class NexusPipeReaderTests
{
    [Test]
    public async Task TryRead_NoData_ReturnsFalseAndEmptyBuffer()
    {
        var reader = CreateReader();
        var got = reader.TryRead(out var result);

        await Assert.That(got).IsFalse().Because("Should get false when there's no buffered data");
        await Assert.That(result.Buffer.IsEmpty).IsTrue().Because("Buffer should be empty");
        await Assert.That(result.IsCanceled).IsFalse().Because("Should not be canceled");
        await Assert.That(result.IsCompleted).IsFalse().Because("Should not be completed");
    }
    
    [Test]
    public async Task BufferData_SmallData_SucceedsAndTryReadReturnsData()
    {
        var reader = CreateReader();
        byte[] payload = [1, 2, 3, 4];

        var bufferResult = reader.BufferData(new ReadOnlySequence<byte>(payload));
        using (Assert.Multiple())
        {
            await Assert.That(bufferResult).IsEqualTo(NexusPipeBufferResult.Success);
            await Assert.That(reader.TryRead(out var readResult)).IsTrue().Because("TryRead should return true after buffering");
            var actual = readResult.Buffer.ToArray();
            await Assert.That(actual).IsEquivalentTo(payload, CollectionOrdering.Matching).Because("Payload should match what was buffered");
            await Assert.That(readResult.IsCanceled).IsFalse();
            await Assert.That(readResult.IsCompleted).IsFalse();
        }

    }
    
    
    [Test]
    public async Task CancelPendingRead_ReadAsyncReturnsCanceled()
    {
        var reader = CreateReader();

        reader.CancelPendingRead();
        var readResult = await reader.ReadAsync();

        await Assert.That(readResult.IsCanceled).IsTrue().Because("ReadAsync should reflect cancellation");
        await Assert.That(readResult.Buffer.Length).IsEqualTo(0).Because("Buffer should still be empty");
        await Assert.That(readResult.IsCompleted).IsFalse();
    }
    
    
    [Test]
    public async Task CompleteAsync_SetsIsCompletedAndTryReadReturnsCompleted()
    {
        var reader = CreateReader();

        await reader.CompleteAsync();
        await Assert.That(reader.IsCompleted).IsTrue().Because("Reader should be marked completed");

        await Assert.That(reader.TryRead(out var result)).IsTrue();
        await Assert.That(result.IsCompleted).IsTrue();
        await Assert.That(result.IsCanceled).IsFalse();
        await Assert.That(result.Buffer.IsEmpty).IsTrue().Because("No data should be buffered");
    }
    
    [Test]
    public async Task AdvanceTo_ConsumesData_TryReadReturnsFalse()
    {
        var reader = CreateReader();
        byte[] payload = [5, 6, 7, 8];

        reader.BufferData(new ReadOnlySequence<byte>(payload));
        var readResult = await reader.ReadAsync();

        await Assert.That(readResult.Buffer.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching).Because("Initial read sees full payload");

        reader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);

        await Assert.That(reader.TryRead(out var afterConsume)).IsFalse().Because("No data should remain after consume");
        await Assert.That(afterConsume.Buffer.IsEmpty).IsTrue();
        await Assert.That(afterConsume.IsCompleted).IsFalse();
        await Assert.That(afterConsume.IsCanceled).IsFalse();
    }
    
    
    
    
    
    [Test]
    public async Task BufferData_AfterCompleteNoNotify_ReturnsDataIgnored()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CompleteNoNotify();

        var result = reader.BufferData(new ReadOnlySequence<byte>([1, 2, 3]));
        await Assert.That(result).IsEqualTo(NexusPipeBufferResult.DataIgnored).Because("Once completed, new data must be ignored");
    }
    
    
    [Test]
    public async Task ReadAsync_AfterCompleteNoNotify_ReturnsCompleted()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CompleteNoNotify();

        var rr = await reader.ReadAsync();
        await Assert.That(rr.IsCompleted).IsTrue();
        await Assert.That(rr.Buffer.IsEmpty).IsTrue();
        await Assert.That(rr.IsCanceled).IsFalse();
    }
    
    [Test]
    public async Task TryRead_AfterCancelPendingRead_ReturnsTrueAndCanceled()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CancelPendingRead();
        var got = reader.TryRead(out var result);

        await Assert.That(got).IsTrue();
        await Assert.That(result.IsCanceled).IsTrue();
        await Assert.That(result.Buffer.IsEmpty).IsTrue();
        await Assert.That(result.IsCompleted).IsFalse();
    }
    
    [Test]
    public async Task TryRead_CancellationFlagResets_AfterFirstTryRead()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CancelPendingRead();

        // First TryRead picks up the cancel flag…
        await Assert.That(reader.TryRead(out _)).IsTrue();

        // …but it is cleared immediately thereafter
        var secondCall = reader.TryRead(out var r2);
        await Assert.That(secondCall).IsFalse().Because("No more data & no more cancellation");
        await Assert.That(r2.IsCanceled).IsFalse();
    }
    
    [Test]
    public async Task CompleteAsync_CallsStateManagerUpdateAndNotify()
    {
        var spy = new SpyStateManager();
        // isServer = false → writingCompleteFlag == ClientReaderServerWriterComplete
        var reader = TestPipes.Reader(spy, null, false);

        await reader.CompleteAsync();

        await Assert.That(spy.Updates).Count().IsEqualTo(1);
        await Assert.That(spy.Updates[0].Flag).IsEqualTo(NexusDuplexPipe.State.ClientReaderServerWriterComplete);
        await Assert.That(spy.Updates[0].Remove).IsFalse();
        await Assert.That(spy.NotifyCount).IsEqualTo(1);

        // And TryRead now reports IsCompleted
        await Assert.That(reader.TryRead(out var finalResult)).IsTrue();
        await Assert.That(finalResult.IsCompleted).IsTrue();
    }
    
    [Test]
    public async Task CancelPendingRead_DoesNotUpdateState()
    {
        var spy = new SpyStateManager();
        var reader = TestPipes.Reader(spy, null, false);

        reader.CancelPendingRead();

        await Assert.That(spy.Updates).IsEmpty().Because("CancelPendingRead should not call UpdateState");
        await Assert.That(spy.NotifyCount).IsZero().Because("CancelPendingRead should not call NotifyState");
    }
    
    
    [Test]
    public async Task CompleteAsync_AsServer_UsesClientWriterServerReaderCompleteFlag()
    {
        var spy = new SpyStateManager();
        // isServer = true → writingCompleteFlag == ClientWriterServerReaderComplete
        var reader = TestPipes.Reader(spy, null, true);

        await reader.CompleteAsync();

        await Assert.That(spy.Updates).Count().IsEqualTo(1);
        await Assert.That(spy.Updates[0].Flag).IsEqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete).Because("Server readers should set the ClientWriterServerReaderComplete flag");
        await Assert.That(spy.NotifyCount).IsEqualTo(1);
    }
    
    
    [Test]
    public async Task BufferData_BeyondCredit_ReturnsCreditExceeded()
    {
        var reader = CreateReader(window: 4);

        await Assert.That(reader.BufferData(new ReadOnlySequence<byte>([1, 2, 3]))).IsEqualTo(NexusPipeBufferResult.Success);
        await Assert.That(reader.BufferData(new ReadOnlySequence<byte>([4, 5]))).IsEqualTo(NexusPipeBufferResult.CreditExceeded);

        // The data beyond the credit was not buffered.
        await Assert.That(reader.TryRead(out var result)).IsTrue();
        await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task BufferData_BeforeTheWindowOpens_ReturnsCreditExceeded()
    {
        // Nothing was granted yet, so the peer may not send anything.
        var reader = new NexusPipeReader(new DummyStateManager(), logger: null, isServer: false, window: 1024);

        await Assert.That(reader.BufferData(new ReadOnlySequence<byte>([1]))).IsEqualTo(NexusPipeBufferResult.CreditExceeded);
    }

    [Test]
    public async Task ConsumingHalfTheWindow_GrantsTheConsumedBytes()
    {
        var spy = new SpyStateManager();
        var reader = TestPipes.Reader(spy, window: 8);
        reader.BufferData(new ReadOnlySequence<byte>([0, 1, 2, 3, 4, 5, 6, 7]));

        reader.AdvanceTo(3, 3);
        await Assert.That(spy.Grants).IsEmpty().Because("Less than half the window consumed");

        reader.AdvanceTo(1, 1);
        await Assert.That(spy.Grants).IsEquivalentTo(new[] { 4 }, CollectionOrdering.Matching).Because("Half the window consumed");

        // The grant is usable: 4 more bytes fit, a fifth does not.
        await Assert.That(reader.BufferData(new ReadOnlySequence<byte>([8, 9, 10, 11]))).IsEqualTo(NexusPipeBufferResult.Success);
        await Assert.That(reader.BufferData(new ReadOnlySequence<byte>([12]))).IsEqualTo(NexusPipeBufferResult.CreditExceeded);
    }

    [Test]
    public async Task BufferData_AfterComplete_IsDiscardedButCountsAgainstTheCredit()
    {
        var spy = new SpyStateManager();
        var reader = TestPipes.Reader(spy, window: 4);
        reader.CompleteNoNotify();

        // The peer may still have data in flight; it is discarded, up to the credit granted.
        await Assert.That(reader.BufferData(new ReadOnlySequence<byte>([1, 2, 3]))).IsEqualTo(NexusPipeBufferResult.DataIgnored);
        await Assert.That(reader.BufferData(new ReadOnlySequence<byte>([4, 5]))).IsEqualTo(NexusPipeBufferResult.CreditExceeded);
        await Assert.That(spy.Grants).IsEmpty().Because("Discarded data is not granted back");
    }

    private NexusPipeReader CreateReader(int window = TestPipes.LargeWindow, bool isServer = false)
    {
        return TestPipes.Reader(new DummyStateManager(), isServer: isServer, window: window);
    }
    
    /// <summary>
    /// A spy implementation of IPipeStateManager that records all UpdateState calls
    /// and NotifyState invocations for later verification.
    /// </summary>
    private class SpyStateManager : IPipeStateManager
    {
        public ushort Id => 2;
        public NexusDuplexPipe.State CurrentState { get; set; } = default;

        public List<(NexusDuplexPipe.State Flag, bool Remove)> Updates { get; } = [];
        public int NotifyCount { get; private set; }

        public bool UpdateState(NexusDuplexPipe.State flag, bool remove = false)
        {
            Updates.Add((flag, remove));
            if (remove) CurrentState &= ~flag;
            else         CurrentState |= flag;
            return true;
        }

        public ValueTask NotifyState()
        {
            NotifyCount++;
            return new ValueTask();
        }

        public List<int> Grants { get; } = [];

        public void GrantCredit(int bytes) => Grants.Add(bytes);
    }
    
    /// <summary>
    /// A minimal in‑memory stub of IPipeStateManager
    /// that always accepts state updates and no‑ops on NotifyState().
    /// </summary>
    private class DummyStateManager : IPipeStateManager
    {
        public ushort Id => 1;
        public NexusDuplexPipe.State CurrentState { get; set; }

        public bool UpdateState(NexusDuplexPipe.State flag, bool remove = false)
        {
            if (remove)
                CurrentState &= ~flag;
            else
                CurrentState |= flag;
            return true;
        }

        public ValueTask NotifyState() => new ValueTask(); 
    }
}