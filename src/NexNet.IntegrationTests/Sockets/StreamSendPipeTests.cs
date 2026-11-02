using System.IO.Pipelines;
using NexNet.Internals.Pipelines;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Sockets;

internal class StreamSendPipeTests
{
    [Test]
    public async Task FlushCompletesWhileStreamWriteIsPending()
    {
        var stream = new RecordingStream { Gated = true };
        var sendPipe = new StreamSendPipe(stream, null);

        var flush = Write(sendPipe.Writer, Frame(1, 100));
        Assert.That(flush.IsCompleted, Is.True);
        await stream.WriteStarted.Task.Timeout(1);
        Assert.That(stream.Writes, Is.Empty);

        stream.Release();
        await CompleteAndDrain(sendPipe);
        Assert.That(stream.Writes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task FramesQueuedDuringAStreamWriteGoOutInOneWrite()
    {
        var stream = new RecordingStream { Gated = true };
        var sendPipe = new StreamSendPipe(stream, null);

        var first = Frame(0, 100);
        await Write(sendPipe.Writer, first);
        await stream.WriteStarted.Task.Timeout(1);

        // 10 frames of 3000 bytes span several pipe segments; they are coalesced into one write.
        var expected = new List<byte>();
        for (byte i = 1; i <= 10; i++)
        {
            var frame = Frame(i, 3000);
            expected.AddRange(frame);
            await Write(sendPipe.Writer, frame);
        }

        stream.Release();
        await CompleteAndDrain(sendPipe);

        Assert.That(stream.Writes, Has.Count.EqualTo(2));
        Assert.That(stream.Writes[0], Is.EqualTo(first));
        Assert.That(stream.Writes[1], Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public async Task CoalescedWritesAreBoundedAndKeepOrder()
    {
        var stream = new RecordingStream { Gated = true };
        var sendPipe = new StreamSendPipe(stream, new PipeOptions(pauseWriterThreshold: 0));

        await Write(sendPipe.Writer, Frame(0, 10));
        await stream.WriteStarted.Task.Timeout(1);

        var expected = new List<byte>(Frame(0, 10));
        for (var i = 0; i < 100; i++)
        {
            var frame = Frame((byte)i, 3000);
            expected.AddRange(frame);
            await Write(sendPipe.Writer, frame);
        }

        stream.Release();
        await CompleteAndDrain(sendPipe);

        Assert.That(stream.Writes.Select(w => w.Length), Has.All.LessThanOrEqualTo(StreamSendPipe.CoalesceSize));
        Assert.That(stream.Writes.SelectMany(w => w).ToArray(), Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public async Task StreamFailureCompletesTheWriter()
    {
        var stream = new RecordingStream { Fail = true };
        var sendPipe = new StreamSendPipe(stream, null);

        await Write(sendPipe.Writer, Frame(1, 10));
        await sendPipe.Completion.Timeout(1);

        // The next flush reports the transport as gone: completed, or the stream's error.
        bool completed;
        try
        {
            completed = (await Write(sendPipe.Writer, Frame(2, 10))).IsCompleted;
        }
        catch (IOException)
        {
            completed = true;
        }

        Assert.That(completed, Is.True);
    }

    [Test]
    public async Task DrainWithoutCompletedWriterAbortsPendingWrite()
    {
        var stream = new RecordingStream { Gated = true };
        var sendPipe = new StreamSendPipe(stream, null);

        await Write(sendPipe.Writer, Frame(1, 10));
        await stream.WriteStarted.Task.Timeout(1);

        // The session did not complete the output (not a graceful close): nothing is waited for.
        await sendPipe.DrainAsync(TimeSpan.FromSeconds(30)).Timeout(1);
        Assert.That(stream.Writes, Is.Empty);
    }

    [Test]
    public async Task DrainWaitsForBufferedDataAfterWriterCompleted()
    {
        var stream = new RecordingStream { Gated = true };
        var sendPipe = new StreamSendPipe(stream, null);

        await Write(sendPipe.Writer, Frame(1, 10));
        await stream.WriteStarted.Task.Timeout(1);
        await Write(sendPipe.Writer, Frame(2, 10));
        await sendPipe.Writer.CompleteAsync();

        var drain = sendPipe.DrainAsync(TimeSpan.FromSeconds(30)).AsTask();
        Assert.That(drain.IsCompleted, Is.False);

        stream.Release();
        await drain.Timeout(1);
        Assert.That(stream.Writes.SelectMany(w => w).ToArray(), Is.EqualTo(Frame(1, 10).Concat(Frame(2, 10)).ToArray()));
    }

    [Test]
    public async Task DrainGivesUpAfterTimeout()
    {
        var stream = new RecordingStream { Gated = true };
        var sendPipe = new StreamSendPipe(stream, null);

        await Write(sendPipe.Writer, Frame(1, 10));
        await stream.WriteStarted.Task.Timeout(1);
        await sendPipe.Writer.CompleteAsync();

        await sendPipe.DrainAsync(TimeSpan.FromMilliseconds(50)).Timeout(1);
        Assert.That(sendPipe.Completion.IsCompleted, Is.True);
    }

    private static async Task CompleteAndDrain(StreamSendPipe sendPipe)
    {
        await sendPipe.Writer.CompleteAsync();
        await sendPipe.DrainAsync(TimeSpan.FromSeconds(5)).Timeout(1);
    }

    private static ValueTask<FlushResult> Write(PipeWriter writer, byte[] frame)
    {
        frame.CopyTo(writer.GetSpan(frame.Length));
        writer.Advance(frame.Length);
        return writer.FlushAsync();
    }

    private static byte[] Frame(byte value, int length)
    {
        var frame = new byte[length];
        frame.AsSpan().Fill(value);
        return frame;
    }

    /// <summary>
    /// Records each write. While <see cref="Gated"/>, every write waits for <see cref="Release"/> (or cancellation);
    /// with <see cref="Fail"/>, writes throw.
    /// </summary>
    private sealed class RecordingStream : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private readonly List<byte[]> _writes = new();

        public bool Gated { get; init; }
        public bool Fail { get; init; }
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> Writes
        {
            get
            {
                lock (_lock)
                    return _writes.ToList();
            }
        }

        public void Release() => _gate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            if (Fail)
                throw new IOException("Write failed.");
            if (Gated)
                await _gate.Task.WaitAsync(cancellationToken);
            lock (_lock)
                _writes.Add(buffer.ToArray());
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
