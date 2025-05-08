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

        var bufferResult = await reader.BufferData(new ReadOnlySequence<byte>(payload));
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
    public async Task BufferData_ExceedsHighWatermark_ReturnsHighWatermarkReached()
    {
        var reader = CreateReader(highWaterMark: 2);
        byte[] payload = [10, 20, 30];

        var bufferResult = await reader.BufferData(new ReadOnlySequence<byte>(payload));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bufferResult, Is.EqualTo(NexusPipeBufferResult.HighWatermarkReached));
            Assert.That(reader.TryRead(out var readResult), Is.True, "Data should still be buffered after high‐watermark");
            Assert.That(readResult.Buffer.ToArray(), Is.EqualTo(payload));
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

        await reader.BufferData(new ReadOnlySequence<byte>(payload));
        var readResult = await reader.ReadAsync();

        Assert.That(readResult.Buffer.ToArray(), Is.EqualTo(payload), "Initial read sees full payload");

        reader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);

        Assert.That(reader.TryRead(out var afterConsume), Is.False, "No data should remain after consume");
        Assert.That(afterConsume.Buffer.IsEmpty, Is.True);
        Assert.That(afterConsume.IsCompleted, Is.False);
        Assert.That(afterConsume.IsCanceled, Is.False);
    }
    
    [Test]
    public async Task BufferData_HighWaterMark_SetsBackPressureFlag()
    {
        var spy = new SpyStateManager();
        // highWaterMark=2, no cutoff or low‐water logic needed here
        var reader = new NexusPipeReader(
            spy, logger: null,
            isServer: false,
            highWaterMark: 2,
            highWaterCutoff: 0,
            lowWaterMark: 0);

        var payload = new byte[] { 0, 1, 2 };
        var result = await reader.BufferData(new ReadOnlySequence<byte>(payload));

        Assert.That(result, Is.EqualTo(NexusPipeBufferResult.HighWatermarkReached));
        Assert.That(spy.Updates, Has.Count.EqualTo(1));
        Assert.That(spy.Updates[0].Flag,
            Is.EqualTo(NexusDuplexPipe.State.ServerWriterPause),
            "Non‐server readers should use the ServerWriterPause flag");
        Assert.That(spy.Updates[0].Remove, Is.False);
        Assert.That(spy.NotifyCount, Is.EqualTo(1),
            "NotifyState must be called exactly once after hitting the watermark");
    }
    
    
    [Test]
    public async Task ReadAsync_BufferDropsBelowLowWaterMark_RemovesBackPressureFlag()
    {
        var spy = new SpyStateManager();
        // highWaterMark=2 to set the flag, lowWaterMark=1 so we can drop below it
        var reader = new NexusPipeReader(
            spy, logger: null,
            isServer: false,
            highWaterMark: 2,
            highWaterCutoff: 0,
            lowWaterMark: 1);

        // 1) Buffer enough to hit the high watermark
        await reader.BufferData(new ReadOnlySequence<byte>([9, 8, 7]));
        // flag set in spy.Updates[0]

        // 2) Read once (does not remove the flag since bufferLength (3) > lowWaterMark (1))
        var firstRead = await reader.ReadAsync();
        Assert.That(firstRead.Buffer.ToArray(), Is.EqualTo(new byte[] { 9, 8, 7 }));

        // 3) Consume 2 bytes → remaining payload length = 1, which is ≤ lowWaterMark
        reader.AdvanceTo(2, 2);

        // 4) Next ReadAsync should see remaining single byte AND remove the back‐pressure flag
        var secondRead = await reader.ReadAsync();
        Assert.That(secondRead.Buffer.ToArray(), Is.EqualTo(new byte[] { 7 }),
            "After advancing, only the last byte should remain");
        Assert.That(spy.Updates, Has.Count.EqualTo(2),
            "Second UpdateState invocation should remove the flag");
        var removalCall = spy.Updates[1];
        Assert.That(removalCall.Flag,
            Is.EqualTo(NexusDuplexPipe.State.ServerWriterPause));
        Assert.That(removalCall.Remove, Is.True,
            "The second UpdateState must indicate removal");
        Assert.That(spy.NotifyCount, Is.EqualTo(2),
            "NotifyState should have been called again for the removal");
    }
    
    
    [Test]
    public async Task BufferData_AfterCompleteNoNotify_ReturnsDataIgnored()
    {
        var reader = new NexusPipeReader(
            new DummyStateManager(), logger: null,
            isServer: false,
            highWaterMark: 0,
            highWaterCutoff: 10,
            lowWaterMark: 0);

        reader.CompleteNoNotify();

        var result = await reader.BufferData(new ReadOnlySequence<byte>([1, 2, 3]));
        Assert.That(result, Is.EqualTo(NexusPipeBufferResult.DataIgnored),
            "Once completed, new data must be ignored");
    }
    
    
    [Test]
    public async Task ReadAsync_AfterCompleteNoNotify_ReturnsCompleted()
    {
        var reader = new NexusPipeReader(
            new DummyStateManager(), logger: null,
            isServer: false,
            highWaterMark: 0,
            highWaterCutoff: 0,
            lowWaterMark: 0);

        reader.CompleteNoNotify();

        var rr = await reader.ReadAsync();
        Assert.That(rr.IsCompleted, Is.True);
        Assert.That(rr.Buffer.IsEmpty, Is.True);
        Assert.That(rr.IsCanceled, Is.False);
    }
    
    [Test]
    public void TryRead_AfterCancelPendingRead_ReturnsTrueAndCanceled()
    {
        var reader = new NexusPipeReader(
            new DummyStateManager(), logger: null,
            isServer: false,
            highWaterMark: 0,
            highWaterCutoff: 0,
            lowWaterMark: 0);

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
        var reader = new NexusPipeReader(
            new DummyStateManager(), logger: null,
            isServer: false,
            highWaterMark: 0,
            highWaterCutoff: 0,
            lowWaterMark: 0);

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
        var reader = new NexusPipeReader(
            spy, logger: null,
            isServer: false,
            highWaterMark: 0,
            highWaterCutoff: 0,
            lowWaterMark: 0);

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
        var reader = new NexusPipeReader(
            spy, logger: null,
            isServer: false,
            highWaterMark: 0,
            highWaterCutoff: 0,
            lowWaterMark: 0);

        reader.CancelPendingRead();

        Assert.That(spy.Updates, Is.Empty,    "CancelPendingRead should not call UpdateState");
        Assert.That(spy.NotifyCount, Is.Zero,  "CancelPendingRead should not call NotifyState");
    }
    
    [Test]
    public async Task BufferData_HighWaterCutoffWithoutHighWaterMark_DoesNotTriggerStateUpdates()
    {
        var spy = new SpyStateManager();
        // highWaterCutoff = 2, lowWaterMark = 5 → triggers cutoff loop then exits immediately
        var reader = new NexusPipeReader(
            spy, logger: null,
            isServer: false,
            highWaterMark: 0,
            highWaterCutoff: 2,
            lowWaterMark: 5);

        var result = await reader.BufferData(new ReadOnlySequence<byte>([1, 2]));

        Assert.That(result, Is.EqualTo(NexusPipeBufferResult.Success));
        Assert.That(spy.Updates, Is.Empty,   "No back‑pressure or completion flags should be set");
        Assert.That(spy.NotifyCount, Is.Zero, "NotifyState should never be called");
    }
    
    [Test]
    public async Task CompleteAsync_AsServer_UsesClientWriterServerReaderCompleteFlag()
    {
        var spy = new SpyStateManager();
        // isServer = true → writingCompleteFlag == ClientWriterServerReaderComplete
        var reader = new NexusPipeReader(
            spy, logger: null,
            isServer: true,
            highWaterMark: 0,
            highWaterCutoff: 0,
            lowWaterMark: 0);

        await reader.CompleteAsync();

        Assert.That(spy.Updates, Has.Count.EqualTo(1));
        Assert.That(spy.Updates[0].Flag,
            Is.EqualTo(NexusDuplexPipe.State.ClientWriterServerReaderComplete),
            "Server readers should set the ClientWriterServerReaderComplete flag");
        Assert.That(spy.NotifyCount, Is.EqualTo(1));
    }
    
    [Test]
    public async Task BufferDataAndLowWatermark_WithServer_BackPressureAddsAndRemovesCorrectFlag()
    {
        var spy = new SpyStateManager();
        // For server: backPressureFlag = ClientWriterPause
        var reader = new NexusPipeReader(
            spy, logger: null,
            isServer: true,
            highWaterMark: 3,
            highWaterCutoff: 0,
            lowWaterMark: 1);

        // 1) Buffer enough to hit the watermark
        var highResult = await reader.BufferData(new ReadOnlySequence<byte>([9, 8, 7]));
        Assert.That(highResult, Is.EqualTo(NexusPipeBufferResult.HighWatermarkReached));

        // Should have set ClientWriterPause
        Assert.That(spy.Updates.Any(u =>
                u.Flag == NexusDuplexPipe.State.ClientWriterPause && u.Remove == false),
            Is.True);

        // 2) Read it out
        var read1 = await reader.ReadAsync();
        Assert.That(read1.Buffer.ToArray(), Is.EqualTo(new byte[] { 9, 8, 7 }));

        // 3) Consume 2 bytes → leaves 1, which is ≤ lowWaterMark
        reader.AdvanceTo(2, 2);

        // 4) Next ReadAsync should remove the flag
        var read2 = await reader.ReadAsync();
        Assert.That(read2.Buffer.ToArray(), Is.EqualTo(new byte[] { 7 }));

        Assert.That(spy.Updates.Any(u =>
                u.Flag == NexusDuplexPipe.State.ClientWriterPause && u.Remove),
            Is.True,
            "Once buffer ≤ lowWaterMark, the back‑pressure flag should be removed");
        Assert.That(spy.NotifyCount, Is.EqualTo(2),
            "Should notify once for add and once for removal");
    }
    
    private NexusPipeReader CreateReader(
        int highWaterMark = 0,
        int highWaterCutoff = 0,
        int lowWaterMark = 0,
        bool isServer = false)
    {
        return new NexusPipeReader(
            new DummyStateManager(),
            logger: null,
            isServer: isServer,
            highWaterMark: highWaterMark,
            highWaterCutoff: highWaterCutoff,
            lowWaterMark: lowWaterMark);
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
