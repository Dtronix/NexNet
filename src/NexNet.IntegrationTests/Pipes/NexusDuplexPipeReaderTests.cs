using System.Buffers;
using NexNet.Pipes;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

internal class NexusDuplexPipeReaderTests
{
    private NexusPipeReader CreateReader(IPipeStateManager? stateManager = null)
    {
        stateManager ??= new PipeStateManagerStub();
        var reader = new NexusPipeReader(
            stateManager,
            null,
            true,
            1024 * 128,
            1024 * 1024,
            1024 * 32);
        return reader;
    }

    [Test]
    public async Task BufferData()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();
        await reader.BufferData(new ReadOnlySequence<byte>(simpleData)).Timeout(1);

        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync().Timeout(1);
            Assert.That(readData.Buffer.Length, Is.EqualTo(10));
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ReadAsyncWaitsForData()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync().Timeout(1);
            Assert.That(readData.Buffer.Length, Is.EqualTo(10));
            tcs.SetResult();
        });

        await reader.BufferData(new ReadOnlySequence<byte>(simpleData)).Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ReadAsyncPausesUntilNewDataIsReceived()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync().Timeout(1);
            reader.AdvanceTo(readData.Buffer.Start, readData.Buffer.End);
            Assert.That(readData.Buffer.Length, Is.EqualTo(10));

            await reader.ReadAsync().AsTask().AssertTimeout(0.1);
            tcs.SetResult();
        });


        await reader.BufferData(new ReadOnlySequence<byte>(simpleData)).Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ReadAsyncReadsOnEachNewReceive()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bufferSemaphore = new SemaphoreSlim(0, 1);
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                //Console.WriteLine("Writer");
                await reader.BufferData(new ReadOnlySequence<byte>(new[] { (byte)i })).Timeout(1);
                await bufferSemaphore.WaitAsync().Timeout(1);
            }

        });

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                //Console.WriteLine("Reader");
                var task = reader.ReadAsync().AsTask();
                reader.AdvanceTo(task.Result.Buffer.Start, task.Result.Buffer.End);
              
                var data = await task.Timeout(1);
                Assert.That(data.Buffer.Length, Is.EqualTo(i + 1));
                bufferSemaphore.Release(1);
                //Console.WriteLine("Writer Released");
            }

            tcs.SetResult();
        });


        await tcs.Task.Timeout(1);
    }


    [Test]
    public async Task CancelPendingReadCancelsReadBeforeReadAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = CreateReader();

        reader.CancelPendingRead();

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync().Timeout(1);
            Assert.That(data.IsCanceled, Is.True);
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadCancelsReadBeforeTryRead()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = CreateReader();

        reader.CancelPendingRead();

        _ = Task.Run(() =>
        {
            Assert.That(reader.TryRead(out var data), Is.True);
            Assert.That(data.IsCanceled, Is.True);
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadCancelsReadAfterReadStart()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync().Timeout(1);
            Assert.That(data.IsCanceled, Is.True);
            tcs.SetResult();
        });

        await Task.Delay(50);

        reader.CancelPendingRead();

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadCancelsAllowsReadAsyncAfter()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync().Timeout(1);
            Assert.That(data.IsCanceled, Is.True);

            data = await reader.ReadAsync().Timeout(1);

            Assert.That(data.Buffer.Length, Is.EqualTo(10));

            tcs.SetResult();
        });

        await Task.Delay(1);

        reader.CancelPendingRead();
        await reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })).Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadPreCancelsAllowsReadAsyncAfter()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = CreateReader();

        reader.CancelPendingRead();
        await reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })).Timeout(1);

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync().Timeout(1);
            Assert.That(data.IsCanceled, Is.True);

            await Task.Delay(10);
            data = await reader.ReadAsync().Timeout(1);

            Assert.That(data.Buffer.Length, Is.EqualTo(10));

            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ReadAsyncIsCanceledByCancellationToken_PriorToRead()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        for (var i = 0; i < 10; i++)
        {
            var result = await reader.ReadAsync(cts.Token).Timeout(1);

            Assert.That(result.IsCanceled, Is.True);
        }
    }

    [Test]
    public async Task ReadAsyncIsCanceledByCancellationToken_PostRead()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();

        // ReSharper disable once MethodSupportsCancellation
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            cts.Cancel();
        });

        for (var i = 0; i < 10; i++)
        {
            var result = await reader.ReadAsync(cts.Token).Timeout(1);
            Assert.That(result.IsCanceled, Is.True);
        }
    }

    [Test]
    public async Task ReadAsyncWillContinueAfterCancelByCancellationToken_Pre()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();

        cts.Cancel();
        await reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })).Timeout(1);

        var result = await reader.ReadAsync(cts.Token).Timeout(1);
        Assert.That(result.IsCanceled, Is.True);

        // ReSharper disable once MethodSupportsCancellation
        result = await reader.ReadAsync().Timeout(1);
        Assert.That(result.IsCanceled, Is.False);
        Assert.That(result.Buffer.Length, Is.EqualTo(10));
    }

    [Test]
    public async Task ReadAsyncWillContinueAfterCancelByCancellationToken_Post()
    {
        var reader = CreateReader();
        var data = new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var cts = new CancellationTokenSource();

        // ReSharper disable once MethodSupportsCancellation
        _ = Task.Run(async () =>
        {
            // ReSharper disable once MethodSupportsCancellation
            await Task.Delay(100);
            cts.Cancel();

            await reader.BufferData(data).Timeout(1);
        });

        var result = await reader.ReadAsync(cts.Token).Timeout(1);
        Assert.That(result.IsCanceled, Is.True);

        result = await reader.ReadAsync().Timeout(1);
        Assert.That(result.IsCanceled, Is.False);
        Assert.That(result.Buffer.Length, Is.EqualTo(10));
    }

    [Test]
    public async Task ReadAsyncAdvance()
    {
        var reader = CreateReader();
        var data = new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var length = data.Length * 9000;
        for (var i = 0; i < 9000; i++)
        {
            await reader.BufferData(data).Timeout(1);
        }
        var result = await reader.ReadAsync().Timeout(1);
        Assert.That(result.Buffer.Length, Is.EqualTo(length));

        var position = result.Buffer.GetPosition(3000 * 16);
        reader.AdvanceTo(position);

        result = await reader.ReadAsync().Timeout(1);
        Assert.That(result.Buffer.Length, Is.EqualTo(length - 3000 * 16));
    }

    [Test]
    public async Task TryReadAdvance()
    {
        var reader = CreateReader();
        var data = new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var length = data.Length * 9000;
        for (var i = 0; i < 9000; i++)
        {
            await reader.BufferData(data).Timeout(1);
        }

        Assert.That(reader.TryRead(out var result), Is.True);
        Assert.That(result.Buffer.Length, Is.EqualTo(length));

        var position = result.Buffer.GetPosition(3000 * 16);
        reader.AdvanceTo(position);

        Assert.That(reader.TryRead(out result), Is.True);
        Assert.That(result.Buffer.Length, Is.EqualTo(length - 3000 * 16));
    }

    [Test]
    public async Task ReaderNotifiesBackPressure_HighWaterMark()
    {
        var stateManager = new PipeStateManagerStub(NexusDuplexPipe.State.Ready);
        var data = new ReadOnlySequence<byte>(new byte[1024]);
        var reader = CreateReader(stateManager);
        for (var i = 0; i < 1025 * 2; i++)
        {
            var result = await reader.BufferData(data).Timeout(1);
            if (result == NexusPipeBufferResult.HighWatermarkReached)
            {
                Assert.That(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause), Is.True);
                return;
            }
        }

        Assert.Fail("High water mark not reached.");
    }

    [Test]
    public async Task ReaderNotifiesBackPressure_HighWaterCutoff()
    {
        var stateManager = new PipeStateManagerStub(NexusDuplexPipe.State.Ready);
        var data = new ReadOnlySequence<byte>(new byte[1024]);
        stateManager ??= new PipeStateManagerStub();
        var reader = new NexusPipeReader(
            stateManager,
            null,
            true,
            1024 * 128,
            1024 * 1024,
            1024 * 32);
        for (var i = 0; i < 1023; i++)
        {
            await reader.BufferData(data).Timeout(1);
        }

        await reader.BufferData(data).AsTask().AssertTimeout(.15);
    }

    [Test]
    public async Task ReadAsyncNotifiesBackPressure_ReachesLowWaterMark()
    {
        var stateManager = new PipeStateManagerStub(NexusDuplexPipe.State.Ready);
        var data = new ReadOnlySequence<byte>(new byte[1024]);
        stateManager ??= new PipeStateManagerStub();
        var reader = new NexusPipeReader(
            stateManager,
            null,
            true,
            1024 * 128,
            1024 * 1024,
            1024 * 32);



        for (var j = 0; j < 10; j++)
        {
            // Reach the high water mark.
            for (var i = 0; i < 128; i++)
            {
                await reader.BufferData(data).Timeout(1);
            }

            Assert.That(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause), Is.True);

            for (var i = 0; i < 96; i++)
            {
                var result = await reader.ReadAsync().Timeout(1);
                reader.AdvanceTo(result.Buffer.GetPosition(1024));
                Assert.That(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause), Is.True);
            }

            for (var i = 0; i < 32; i++)
            {
                var result = await reader.ReadAsync().Timeout(1);
                reader.AdvanceTo(result.Buffer.GetPosition(1024));
                Assert.That(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause), Is.False);
            }

        }
    }


    [Test]
    public void TryReadReturnsFalseWithNoData()
    {
        var reader = CreateReader();

        Assert.That(reader.TryRead(out var readData), Is.False);
        Assert.That(readData.Buffer.Length, Is.EqualTo(0));
    }

    [Test]
    public async Task TryReadReturnsTrueWithNewData()
    {
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();
        await reader.BufferData(new ReadOnlySequence<byte>(simpleData)).Timeout(1);
        Assert.That(reader.TryRead(out var readData), Is.True);
    }

    [Test]
    public void TryReadReturnsTrueWhenCanceled()
    {
        var reader = CreateReader();
        reader.CancelPendingRead();
        Assert.That(reader.TryRead(out var readData), Is.True);
        Assert.That(readData.IsCanceled, Is.True);
        Assert.That(readData.IsCompleted, Is.False);
    }

    [Test]
    public void TryReadReturnsTrueWhenCompleted()
    {
        var reader = CreateReader();
        reader.CompleteNoNotify();
        Assert.That(reader.TryRead(out var readData), Is.True);
        Assert.That(readData.IsCanceled, Is.False);
        Assert.That(readData.IsCompleted, Is.True);
    }
}
