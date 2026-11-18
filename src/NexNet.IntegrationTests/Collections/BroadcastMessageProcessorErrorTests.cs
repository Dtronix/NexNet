using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NexNet.Collections;
using NexNet.Collections.Lists;
using NexNet.Logging;
using NexNet.Pipes.Broadcast;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Collections;

/// <summary>
/// An unexpected error escaping the broadcast processing loop used to leave the loop spinning on its closed
/// channel until the run was cancelled, and a restart then threw "Broadcaster is already running." (S09).
/// </summary>
internal class BroadcastMessageProcessorErrorTests
{
    [Test]
    public async Task UnexpectedError_StopsLoop_WithoutSpinning_AndRestartWorks()
    {
        var logger = new CountingLogger();
        var shouldThrow = true;
        var processed = 0;
        var processor = new NexusBroadcastMessageProcessor<INexusCollectionListMessage>(logger, (_, _, _) =>
        {
            if (Volatile.Read(ref shouldThrow))
                throw new InvalidOperationException("Injected handler failure.");

            Interlocked.Increment(ref processed);
            return new ValueTask<BroadcastMessageProcessResult>(new BroadcastMessageProcessResult(true, false));
        });

        // The run's token is never cancelled: only the error may end the loop.
        using var runCts = new CancellationTokenSource();
        processor.Run(runCts.Token);

        // The handler throws, then completing the client's pipe throws too, which escapes the per-message catch.
        var result = await processor
            .EnqueueWaitForResult(NexusCollectionListNoopMessage.Rent(), new ThrowingSession())
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(result, Is.False);

        // The loop task itself ends (it used to keep spinning on the closed channel until the token was
        // cancelled), and the run is marked stopped.
        await processor.LoopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(await processor.StoppedTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(logger.Messages.Count(m => m.Level == NexusLogLevel.Error), Is.EqualTo(1));

        // Enqueue on the stopped run is rejected instead of hanging.
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(async () =>
            await processor.EnqueueWaitForResult(NexusCollectionListNoopMessage.Rent(), null));

        // A restart succeeds without cancelling the faulted run, and processes messages again.
        Volatile.Write(ref shouldThrow, false);
        processor.Run(runCts.Token);
        var restarted = await processor
            .EnqueueWaitForResult(NexusCollectionListNoopMessage.Rent(), null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(restarted, Is.True);
            Assert.That(Volatile.Read(ref processed), Is.EqualTo(1));
        });

        await runCts.CancelAsync();
        Assert.That(await processor.StoppedTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
    }

    /// <summary>
    /// Records every log call, from any child logger, regardless of level.
    /// </summary>
    private sealed class CountingLogger : INexusLogger
    {
        private readonly ConcurrentQueue<(NexusLogLevel Level, string Message)> _messages;

        public CountingLogger() : this(new ConcurrentQueue<(NexusLogLevel, string)>())
        {
        }

        private CountingLogger(ConcurrentQueue<(NexusLogLevel, string)> messages) => _messages = messages;

        public IEnumerable<(NexusLogLevel Level, string Message)> Messages => _messages;
        public NexusLogBehaviors Behaviors { get; set; }
        public string FormattedPath => PathSegment ?? "";
        public string? PathSegment { get; set; }

        public void Log(NexusLogLevel logLevel, string? category, Exception? exception, string message)
            => _messages.Enqueue((logLevel, message));

        public INexusLogger CreateLogger(string? pathSegment = null) => new CountingLogger(_messages);
    }

    private sealed class ThrowingSession : INexusBroadcastSession<INexusCollectionListMessage>
    {
        public long Id => 1;
        public CancellationToken CompletionToken => CancellationToken.None;
        public INexusLogger? Logger => null;

        public ValueTask CompletePipe() => throw new InvalidOperationException("Injected CompletePipe failure.");

        public ValueTask<bool> SendAsync(INexusCollectionListMessage message, CancellationToken ct = default)
            => new(false);

        public bool BufferTryWrite(INexusCollectionBroadcasterMessageWrapper<INexusCollectionListMessage> message)
            => false;

        public async IAsyncEnumerable<INexusCollectionBroadcasterMessageWrapper<INexusCollectionListMessage>> BufferRead(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
