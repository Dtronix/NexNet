using System.Buffers;
using NexNet.Pipes;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

internal class NexusDuplexPipeReaderTests
{
    private NexusPipeReader CreateReader(IPipeStateManager? stateManager = null, int window = TestPipes.LargeWindow)
    {
        stateManager ??= new PipeStateManagerStub();
        return TestPipes.Reader(stateManager, window: window);
    }

    [Test]
    public async Task BufferData_WithinTheWindow_NeverWaits()
    {
        // The whole window can be buffered while nothing reads it: buffering is synchronous and only ever bounded by
        // the credit granted. (The read loop used to wait here for the reader to drain.)
        var reader = CreateReader(window: 1024 * 1024);
        var data = new ReadOnlySequence<byte>(new byte[1024]);
        for (var i = 0; i < 1024; i++)
            await Assert.That(reader.BufferData(data)).IsEqualTo(NexusPipeBufferResult.Success);

        await Assert.That(reader.BufferData(data)).IsEqualTo(NexusPipeBufferResult.CreditExceeded);
    }

    [Test]
    public async Task BufferData()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();
        reader.BufferData(new ReadOnlySequence<byte>(simpleData));

        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync().Timeout(1);
            await Assert.That(readData.Buffer.Length).IsEqualTo(10);
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
            await Assert.That(readData.Buffer.Length).IsEqualTo(10);
            tcs.SetResult();
        });

        reader.BufferData(new ReadOnlySequence<byte>(simpleData));

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
            await Assert.That(readData.Buffer.Length).IsEqualTo(10);

            await reader.ReadAsync().AsTask().AssertTimeout(0.1);
            tcs.SetResult();
        });


        reader.BufferData(new ReadOnlySequence<byte>(simpleData));

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
                reader.BufferData(new ReadOnlySequence<byte>(new[] { (byte)i }));
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
                await Assert.That(data.Buffer.Length).IsEqualTo(i + 1);
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
            await Assert.That(data.IsCanceled).IsTrue();
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
            CallbackAssert.That(reader.TryRead(out var data));
            CallbackAssert.That(data.IsCanceled);
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
            await Assert.That(data.IsCanceled).IsTrue();
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
            await Assert.That(data.IsCanceled).IsTrue();

            data = await reader.ReadAsync().Timeout(1);

            await Assert.That(data.Buffer.Length).IsEqualTo(10);

            tcs.SetResult();
        });

        await Task.Delay(1);

        reader.CancelPendingRead();
        reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadPreCancelsAllowsReadAsyncAfter()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = CreateReader();

        reader.CancelPendingRead();
        reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

        _ = Task.Run(async () =>
        {
            var data = await reader.ReadAsync().Timeout(1);
            await Assert.That(data.IsCanceled).IsTrue();

            await Task.Delay(10);
            data = await reader.ReadAsync().Timeout(1);

            await Assert.That(data.Buffer.Length).IsEqualTo(10);

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

            await Assert.That(result.IsCanceled).IsTrue();
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
            await Assert.That(result.IsCanceled).IsTrue();
        }
    }

    [Test]
    public async Task ReadAsyncWillContinueAfterCancelByCancellationToken_Pre()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();

        cts.Cancel();
        reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

        var result = await reader.ReadAsync(cts.Token).Timeout(1);
        await Assert.That(result.IsCanceled).IsTrue();

        // ReSharper disable once MethodSupportsCancellation
        result = await reader.ReadAsync().Timeout(1);
        await Assert.That(result.IsCanceled).IsFalse();
        await Assert.That(result.Buffer.Length).IsEqualTo(10);
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

            reader.BufferData(data);
        });

        var result = await reader.ReadAsync(cts.Token).Timeout(1);
        await Assert.That(result.IsCanceled).IsTrue();

        result = await reader.ReadAsync().Timeout(1);
        await Assert.That(result.IsCanceled).IsFalse();
        await Assert.That(result.Buffer.Length).IsEqualTo(10);
    }

    [Test]
    public async Task ReadAsyncAdvance()
    {
        var reader = CreateReader();
        var data = new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var length = data.Length * 9000;
        for (var i = 0; i < 9000; i++)
        {
            reader.BufferData(data);
        }
        var result = await reader.ReadAsync().Timeout(1);
        await Assert.That(result.Buffer.Length).IsEqualTo(length);

        var position = result.Buffer.GetPosition(3000 * 16);
        reader.AdvanceTo(position);

        result = await reader.ReadAsync().Timeout(1);
        await Assert.That(result.Buffer.Length).IsEqualTo(length - 3000 * 16);
    }

    [Test]
    public async Task TryReadAdvance()
    {
        var reader = CreateReader();
        var data = new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var length = data.Length * 9000;
        for (var i = 0; i < 9000; i++)
        {
            reader.BufferData(data);
        }

        await Assert.That(reader.TryRead(out var result)).IsTrue();
        await Assert.That(result.Buffer.Length).IsEqualTo(length);

        var position = result.Buffer.GetPosition(3000 * 16);
        reader.AdvanceTo(position);

        await Assert.That(reader.TryRead(out result)).IsTrue();
        await Assert.That(result.Buffer.Length).IsEqualTo(length - 3000 * 16);
    }





    [Test]
    public async Task TryReadReturnsFalseWithNoData()
    {
        var reader = CreateReader();

        await Assert.That(reader.TryRead(out var readData)).IsFalse();
        await Assert.That(readData.Buffer.Length).IsEqualTo(0);
    }

    [Test]
    public async Task TryReadReturnsTrueWithNewData()
    {
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();
        reader.BufferData(new ReadOnlySequence<byte>(simpleData));
        await Assert.That(reader.TryRead(out var readData)).IsTrue();
    }

    [Test]
    public async Task TryReadReturnsTrueWhenCanceled()
    {
        var reader = CreateReader();
        reader.CancelPendingRead();
        await Assert.That(reader.TryRead(out var readData)).IsTrue();
        await Assert.That(readData.IsCanceled).IsTrue();
        await Assert.That(readData.IsCompleted).IsFalse();
    }

    [Test]
    public async Task TryReadReturnsTrueWhenCompleted()
    {
        var reader = CreateReader();
        reader.CompleteNoNotify();
        await Assert.That(reader.TryRead(out var readData)).IsTrue();
        await Assert.That(readData.IsCanceled).IsFalse();
        await Assert.That(readData.IsCompleted).IsTrue();
    }
}