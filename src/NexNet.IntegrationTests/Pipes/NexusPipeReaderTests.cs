using System.Buffers;
using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

public class NexusPipeReaderTests
{
    [Test]
    public void TryRead_NoData_ReturnsFalseAndEmptyBuffer()
    {
        var reader = CreateReader();
        var got = reader.TryRead(out var result);

        Assert.That(got, Is.False, "Should get false when there's no buffered data");
        Assert.That(result.Buffer.IsEmpty, Is.True, "Buffer should be empty");
        Assert.That(result.IsCanceled, Is.False, "Should not be canceled");
        Assert.That(result.IsCompleted, Is.False, "Should not be completed");
    }
    
    [Test]
    public async Task BufferData_SmallData_SucceedsAndTryReadReturnsData()
    {
        var reader = CreateReader();
        byte[] payload = [1, 2, 3, 4];

        var bufferResult = reader.BufferData(new ReadOnlySequence<byte>(payload));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bufferResult, Is.EqualTo(NexusPipeBufferResult.Success));
            Assert.That(reader.TryRead(out var readResult), Is.True, "TryRead should return true after buffering");
            var actual = readResult.Buffer.ToArray();
            Assert.That(actual, Is.EqualTo(payload), "Payload should match what was buffered");
            Assert.That(readResult.IsCanceled, Is.False);
            Assert.That(readResult.IsCompleted, Is.False);
        }

    }
    
    
    [Test]
    public async Task CancelPendingRead_ReadAsyncReturnsCanceled()
    {
        var reader = CreateReader();

        reader.CancelPendingRead();
        var readResult = await reader.ReadAsync();

        Assert.That(readResult.IsCanceled, Is.True, "ReadAsync should reflect cancellation");
        Assert.That(readResult.Buffer.Length, Is.EqualTo(0), "Buffer should still be empty");
        Assert.That(readResult.IsCompleted, Is.False);
    }
    
    
    [Test]
    public async Task CompleteAsync_SetsIsCompletedAndTryReadReturnsCompleted()
    {
        var reader = CreateReader();

        await reader.CompleteAsync();
        Assert.That(reader.IsCompleted, Is.True, "Reader should be marked completed");

        Assert.That(reader.TryRead(out var result), Is.True);
        Assert.That(result.IsCompleted, Is.True);
        Assert.That(result.IsCanceled, Is.False);
        Assert.That(result.Buffer.IsEmpty, Is.True, "No data should be buffered");
    }
    
    [Test]
    public async Task AdvanceTo_ConsumesData_TryReadReturnsFalse()
    {
        var reader = CreateReader();
        byte[] payload = [5, 6, 7, 8];

        reader.BufferData(new ReadOnlySequence<byte>(payload));
        var readResult = await reader.ReadAsync();

        Assert.That(readResult.Buffer.ToArray(), Is.EqualTo(payload), "Initial read sees full payload");

        reader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);

        Assert.That(reader.TryRead(out var afterConsume), Is.False, "No data should remain after consume");
        Assert.That(afterConsume.Buffer.IsEmpty, Is.True);
        Assert.That(afterConsume.IsCompleted, Is.False);
        Assert.That(afterConsume.IsCanceled, Is.False);
    }
    
    
    
    
    
    [Test]
    public async Task BufferData_AfterCompleteNoNotify_ReturnsDataIgnored()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CompleteNoNotify();

        var result = reader.BufferData(new ReadOnlySequence<byte>([1, 2, 3]));
        Assert.That(result, Is.EqualTo(NexusPipeBufferResult.DataIgnored),
            "Once completed, new data must be ignored");
    }
    
    
    [Test]
    public async Task ReadAsync_AfterCompleteNoNotify_ReturnsCompleted()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CompleteNoNotify();

        var rr = await reader.ReadAsync();
        Assert.That(rr.IsCompleted, Is.True);
        Assert.That(rr.Buffer.IsEmpty, Is.True);
        Assert.That(rr.IsCanceled, Is.False);
    }
    
    [Test]
    public void TryRead_AfterCancelPendingRead_ReturnsTrueAndCanceled()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CancelPendingRead();
        var got = reader.TryRead(out var result);

        Assert.That(got, Is.True);
        Assert.That(result.IsCanceled, Is.True);
        Assert.That(result.Buffer.IsEmpty, Is.True);
        Assert.That(result.IsCompleted, Is.False);
    }
    
    [Test]
    public void TryRead_CancellationFlagResets_AfterFirstTryRead()
    {
        var reader = TestPipes.Reader(new DummyStateManager(), null, false);

        reader.CancelPendingRead();

        // First TryRead picks up the cancel flag…
        Assert.That(reader.TryRead(out _), Is.True);

        // …but it is cleared immediately thereafter
        var secondCall = reader.TryRead(out var r2);
        Assert.That(secondCall, Is.False, "No more data & no more cancellation");
        Assert.That(r2.IsCanceled, Is.False);
    }
    
    [Test]
    public async Task CompleteAsync_CallsStateManagerUpdateAndNotify()
    {
        var spy = new SpyStateManager();
        // isServer = false → writingCompleteFlag == ClientReaderServerWriterComplete
        var reader = TestPipes.Reader(spy, null, false);

        await reader.CompleteAsync();

        Assert.That(spy.Updates, Has.Count.EqualTo(1));
        Assert.That(spy.Updates[0].Flag,
            Is.EqualTo(NexusDuplexPipe.State.ClientReaderServerWriterComplete));
        Assert.That(spy.Updates[0].Remove, Is.False);
        Assert.That(spy.NotifyCount, Is.EqualTo(1));

        // And TryRead now reports IsCompleted
        Assert.That(reader.TryRead(out var finalResult), Is.True);
        Assert.That(finalResult.IsCompleted, Is.True);
    }
    
    [Test]
    public void CancelPendingRead_DoesNotUpdateState()
    {
        var spy = new SpyStateManager();
        var reader = TestPipes.Reader(spy, null, false);

        reader.CancelPendingRead();

        Assert.That(spy.Updates, Is.Empty,    "CancelPendingRead should not call UpdateState");
        Assert.That(spy.NotifyCount, Is.Zero,  "CancelPendingRead should not call NotifyState");
    }
    
    
    [Test]
    public async Task CompleteAsync_AsServer_UsesClientWriterServerReaderCompleteFlag()
    {
        var spy = new SpyStateManager();
        // isServer = true → writingCompleteFlag == ClientWriterServerReaderComplete
        var reader = TestPipes.Reader(spy, null, true);

        await reader.CompleteAsync();

        Assert.That(spy.Updates, Has.Count.EqualTo(1));
        Assert.That(spy.Updates[0].Flag,
            Is.EqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete),
            "Server readers should set the ClientWriterServerReaderComplete flag");
        Assert.That(spy.NotifyCount, Is.EqualTo(1));
    }
    
    
    [Test]
    public void BufferData_BeyondCredit_ReturnsCreditExceeded()
    {
        var reader = CreateReader(window: 4);

        Assert.That(reader.BufferData(new ReadOnlySequence<byte>([1, 2, 3])), Is.EqualTo(NexusPipeBufferResult.Success));
        Assert.That(reader.BufferData(new ReadOnlySequence<byte>([4, 5])), Is.EqualTo(NexusPipeBufferResult.CreditExceeded));

        // The data beyond the credit was not buffered.
        Assert.That(reader.TryRead(out var result), Is.True);
        Assert.That(result.Buffer.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void BufferData_BeforeTheWindowOpens_ReturnsCreditExceeded()
    {
        // Nothing was granted yet, so the peer may not send anything.
        var reader = new NexusPipeReader(new DummyStateManager(), logger: null, isServer: false, window: 1024);

        Assert.That(reader.BufferData(new ReadOnlySequence<byte>([1])), Is.EqualTo(NexusPipeBufferResult.CreditExceeded));
    }

    [Test]
    public void ConsumingHalfTheWindow_GrantsTheConsumedBytes()
    {
        var spy = new SpyStateManager();
        var reader = TestPipes.Reader(spy, window: 8);
        reader.BufferData(new ReadOnlySequence<byte>([0, 1, 2, 3, 4, 5, 6, 7]));

        reader.AdvanceTo(3, 3);
        Assert.That(spy.Grants, Is.Empty, "Less than half the window consumed");

        reader.AdvanceTo(1, 1);
        Assert.That(spy.Grants, Is.EqualTo(new[] { 4 }), "Half the window consumed");

        // The grant is usable: 4 more bytes fit, a fifth does not.
        Assert.That(reader.BufferData(new ReadOnlySequence<byte>([8, 9, 10, 11])), Is.EqualTo(NexusPipeBufferResult.Success));
        Assert.That(reader.BufferData(new ReadOnlySequence<byte>([12])), Is.EqualTo(NexusPipeBufferResult.CreditExceeded));
    }

    [Test]
    public void BufferData_AfterComplete_IsDiscardedButCountsAgainstTheCredit()
    {
        var spy = new SpyStateManager();
        var reader = TestPipes.Reader(spy, window: 4);
        reader.CompleteNoNotify();

        // The peer may still have data in flight; it is discarded, up to the credit granted.
        Assert.That(reader.BufferData(new ReadOnlySequence<byte>([1, 2, 3])), Is.EqualTo(NexusPipeBufferResult.DataIgnored));
        Assert.That(reader.BufferData(new ReadOnlySequence<byte>([4, 5])), Is.EqualTo(NexusPipeBufferResult.CreditExceeded));
        Assert.That(spy.Grants, Is.Empty, "Discarded data is not granted back");
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
