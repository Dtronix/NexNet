using System.Buffers;
using NexNet.Pipes;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class NexusDuplexPipeReaderTests
{
    private NexusPipeReader CreateReader(IPipeStateManager? stateManager = null)
    {
        stateManager ??= new PipeStateManagerStub();
        var reader = new NexusPipeReader(stateManager);
        reader.Setup(
            null,//new ConsoleLogger(""),
            true,
            1024 * 128,
            1024 * 1024,
            1024 * 32);
        return reader;
    }

    [Test]
    public async Task BufferData()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();
        await reader.BufferData(new ReadOnlySequence<byte>(simpleData));

        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync();
            Assert.AreEqual(10, readData.Buffer.Length);
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ReadAsyncWaitsForData()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync();
            Assert.AreEqual(10, readData.Buffer.Length);
            tcs.SetResult();
        });

        await reader.BufferData(new ReadOnlySequence<byte>(simpleData));

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task TryReadWaitsForData()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();

        _ = Task.Run(() =>
        {
            Assert.True(reader.TryRead(out var readData));
            Assert.AreEqual(10, readData.Buffer.Length);
            tcs.SetResult();
        });

        await reader.BufferData(new ReadOnlySequence<byte>(simpleData));

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ReadAsyncPausesUntilNewDataIsReceived()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync();
            reader.AdvanceTo(readData.Buffer.Start, readData.Buffer.End);
            Assert.AreEqual(10, readData.Buffer.Length);

            await reader.ReadAsync().AsTask().AssertTimeout(0.1);
            tcs.SetResult();
        });


        await reader.BufferData(new ReadOnlySequence<byte>(simpleData));

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ReadAsyncReadsOnEachNewReceive()
    {
        var tcs = new TaskCompletionSource();
        var bufferSemaphore = new SemaphoreSlim(0, 1);
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                //Console.WriteLine("Writer");
                await reader.BufferData(new ReadOnlySequence<byte>(new[] { (byte)i }));
                await bufferSemaphore.WaitAsync();
            }
            
        });

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                //Console.WriteLine("Reader");
                var task = reader.ReadAsync().AsTask();
                reader.AdvanceTo(task.Result.Buffer.Start, task.Result.Buffer.End);
                await task.Timeout(1);
                var data = await task;
                Assert.AreEqual(i + 1, data.Buffer.Length);
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
        var tcs = new TaskCompletionSource();
        var reader = CreateReader();

        reader.CancelPendingRead();

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync();
            Assert.IsTrue(data.IsCanceled);
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadCancelsReadBeforeTryRead()
    {
        var tcs = new TaskCompletionSource();
        var reader = CreateReader();

        reader.CancelPendingRead();

        _ = Task.Run(() =>
        {
            Assert.IsTrue(reader.TryRead(out var data));
            Assert.IsTrue(data.IsCanceled);
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadCancelsReadAfterReadStart()
    {
        var tcs = new TaskCompletionSource();
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync();
            Assert.IsTrue(data.IsCanceled);
            tcs.SetResult();
        });

        await Task.Delay(50);

        reader.CancelPendingRead();

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadCancelsAllowsReadAsyncAfter()
    {
        var tcs = new TaskCompletionSource();
        var reader = CreateReader();

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync();
            Assert.IsTrue(data.IsCanceled);

            data = await reader.ReadAsync();

            Assert.AreEqual(10, data.Buffer.Length);

            tcs.SetResult();
        });

        await Task.Delay(1);

        reader.CancelPendingRead();
        await reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadPreCancelsAllowsReadAsyncAfter()
    {
        var tcs = new TaskCompletionSource();
        var reader = CreateReader();

        reader.CancelPendingRead();
        await reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync();
            Assert.IsTrue(data.IsCanceled);

            await Task.Delay(10);
            data = await reader.ReadAsync();

            Assert.AreEqual(10, data.Buffer.Length);

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

        for (int i = 0; i < 10; i++)
        {
            var result = await reader.ReadAsync(cts.Token);

            Assert.IsTrue(result.IsCanceled);
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

        for (int i = 0; i < 10; i++)
        {
            var result = await reader.ReadAsync(cts.Token);
            Assert.IsTrue(result.IsCanceled);
        }
    }

    [Test]
    public async Task ReadAsyncWillContinueAfterCancelByCancellationToken_Pre()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();

        cts.Cancel();
        await reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

        var result = await reader.ReadAsync(cts.Token);
        Assert.IsTrue(result.IsCanceled);

        // ReSharper disable once MethodSupportsCancellation
        result = await reader.ReadAsync();
        Assert.IsFalse(result.IsCanceled);
        Assert.AreEqual(10, result.Buffer.Length);
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

            await reader.BufferData(data);
        });

        var result = await reader.ReadAsync(cts.Token);
        Assert.IsTrue(result.IsCanceled);

        result = await reader.ReadAsync();
        Assert.IsFalse(result.IsCanceled);
        Assert.AreEqual(10, result.Buffer.Length);
    }

    [Test]
    public async Task ReadAsyncAdvance()
    {
        var reader = CreateReader();
        var data = new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var length = data.Length * 9000;
        for (int i = 0; i < 9000; i++)
        {
            await reader.BufferData(data);
        }
        var result = await reader.ReadAsync();
        Assert.AreEqual(length, result.Buffer.Length);

        var position = result.Buffer.GetPosition(3000 * 16);
        reader.AdvanceTo(position);

        result = await reader.ReadAsync();
        Assert.AreEqual(length - (3000 * 16), result.Buffer.Length);
    }

    [Test]
    public async Task TryReadAdvance()
    {
        var reader = CreateReader();
        var data = new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var length = data.Length * 9000;
        for (int i = 0; i < 9000; i++)
        {
            await reader.BufferData(data);
        }
        
        Assert.IsTrue(reader.TryRead(out var result));
        Assert.AreEqual(length, result.Buffer.Length);

        var position = result.Buffer.GetPosition(3000 * 16);
        reader.AdvanceTo(position);

        Assert.IsTrue(reader.TryRead(out result));
        Assert.AreEqual(length - (3000 * 16), result.Buffer.Length);
    }

    [Test]
    public async Task ReaderNotifiesBackPressure_HighWaterMark()
    {
        var stateManager = new PipeStateManagerStub(NexusDuplexPipe.State.Ready);
        var data = new ReadOnlySequence<byte>(new byte[1024]);
        var reader = CreateReader(stateManager);
        for (int i = 0; i < 1025 * 2; i++)
        {
            var result = await reader.BufferData(data);
            if (result == NexusPipeBufferResult.HighWatermarkReached)
            {
                Assert.IsTrue(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause));
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
        var reader = CreateReader(stateManager);
        reader.Setup(
            null,//new ConsoleLogger(""),
            true,
            1024 * 128,
            1024 * 1024,
            1024 * 32);
        for (int i = 0; i < 1023; i++)
        {
            await reader.BufferData(data);
        }

        await reader.BufferData(data).AsTask().AssertTimeout(.15);
    }

    [Test]
    public async Task ReadAsyncNotifiesBackPressure_ReachesLowWaterMark()
    {
        var stateManager = new PipeStateManagerStub(NexusDuplexPipe.State.Ready);
        var data = new ReadOnlySequence<byte>(new byte[1024]);
        var reader = CreateReader(stateManager);
        reader.Setup(
            null,//new ConsoleLogger(""),
            true,
            1024 * 128,
            1024 * 1024,
            1024 * 32);

        for (int j = 0; j < 10; j++)
        {
            // Reach the high water mark.
            for (int i = 0; i < 128; i++)
            {
                await reader.BufferData(data);
            }

            Assert.IsTrue(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause));

            for (int i = 0; i < 96; i++)
            {
                var result = await reader.ReadAsync();
                reader.AdvanceTo(result.Buffer.GetPosition(1024));
                Assert.IsTrue(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause));
            }

            for (int i = 0; i < 32; i++)
            {
                var result = await reader.ReadAsync();
                reader.AdvanceTo(result.Buffer.GetPosition(1024));
                Assert.IsFalse(stateManager.CurrentState.HasFlag(NexusDuplexPipe.State.ClientWriterPause));
            }

        }
    }


    [Test]
    public void TryReadReturnsFalseWithNoData()
    {
        var reader = CreateReader();

        Assert.IsFalse(reader.TryRead(out var readData));
        Assert.AreEqual(0, readData.Buffer.Length);
    }

    [Test]
    public async Task TryReadReturnsTrueWithNewData()
    {
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();
        await reader.BufferData(new ReadOnlySequence<byte>(simpleData));
        Assert.IsTrue(reader.TryRead(out var readData));
    }

    [Test]
    public void TryReadReturnsTrueWhenCanceled()
    {
        var reader = CreateReader();
        reader.CancelPendingRead();
        Assert.IsTrue(reader.TryRead(out var readData));
        Assert.IsTrue(readData.IsCanceled);
        Assert.False(readData.IsCompleted);
    }

    [Test]
    public void TryReadReturnsTrueWhenCompleted()
    {
        var reader = CreateReader();
        reader.Complete();
        Assert.IsTrue(reader.TryRead(out var readData));
        Assert.IsFalse(readData.IsCanceled);
        Assert.IsTrue(readData.IsCompleted);
    }
}
