using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Testing.Streaming;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.Testing.Tests;

internal class PipeRecordingTests
{
    [Test]
    public async Task TappingPipeReader_RecordsConsumedSliceOnAdvanceTo()
    {
        var pipe = new Pipe();
        var recording = new PipeRecording();
        var reader = new TappingPipeReader(pipe.Reader, recording);

        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });
        await pipe.Writer.CompleteAsync();

        var read = await reader.ReadAsync();
        Assert.That(read.Buffer.Length, Is.EqualTo(5));

        // Advance past 3 bytes; the remaining 2 stay in the buffer.
        reader.AdvanceTo(read.Buffer.GetPosition(3));

        Assert.That(recording.ConsumedBytes.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task TappingPipeWriter_RecordsAdvancedBytes()
    {
        var pipe = new Pipe();
        var recording = new PipeRecording();
        var writer = new TappingPipeWriter(pipe.Writer, recording);

        var mem = writer.GetMemory(4);
        new byte[] { 10, 20, 30, 40 }.CopyTo(mem);
        writer.Advance(4);
        await writer.FlushAsync();

        Assert.That(recording.WrittenBytes.ToArray(), Is.EqualTo(new byte[] { 10, 20, 30, 40 }));
    }

    [Test]
    public async Task WaitForBytesAsync_CompletesAfterRecording()
    {
        var recording = new PipeRecording();
        var waiter = recording.WaitForBytesAsync(3);
        Assert.That(waiter.IsCompleted, Is.False);

        recording.RecordConsumed(new byte[] { 1, 2, 3, 4 });

        await waiter.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Completion_NotifiesWaiters()
    {
        var recording = new PipeRecording();
        var completion = recording.WaitForCompletionAsync();
        Assert.That(completion.IsCompleted, Is.False);

        recording.RecordCompletion(null);

        await completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(recording.IsCompleted, Is.True);
        Assert.That(recording.FaultedWith, Is.Null);
    }

    [Test]
    public void Completion_CarriesException()
    {
        var recording = new PipeRecording();
        var error = new InvalidOperationException("boom");
        recording.RecordCompletion(error);
        Assert.That(recording.IsCompleted, Is.True);
        Assert.That(recording.FaultedWith, Is.SameAs(error));
    }
}
