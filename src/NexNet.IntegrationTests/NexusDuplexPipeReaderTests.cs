﻿using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using MemoryPack;
using NexNet.Cache;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Internals;
using NexNet.Invocation;
using NexNet.Messages;
using NexNet.Transports;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class NexusDuplexPipeReaderTests
{
    /*private class SessionStub : INexusSession
    {
        private class ConsoleLogger : INexusLogger
        {
            public void Log(INexusLogger.LogLevel logLevel, Exception? exception, string message)
            {
                if (logLevel < INexusLogger.LogLevel.Trace)
                    return;

                Console.WriteLine($"{logLevel}: {message} {exception}");
            }
        }
        public long Id { get; set; }
        public List<int> RegisteredGroups { get; set; }
        public Task DisconnectAsync(DisconnectReason reason)
        {
            throw new NotImplementedException();
        }

        public SessionManager? SessionManager { get; set; }
        public SessionStore SessionStore { get; set; }
        public SessionInvocationStateManager SessionInvocationStateManager { get; set; }
        public long LastReceived { get; set; }
        public INexusLogger? Logger { get; set; } = new ConsoleLogger();
        public CacheManager CacheManager { get; set; }
        public ConfigBase Config { get; set; }
        public ConnectionState State { get; set; }
        public bool IsServer { get; set; }
        public NexusPipeManager PipeManager { get; set; }
        public ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default) where TMessage : IMessageBase
        {
            throw new NotImplementedException();
        }

        public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public bool DisconnectIfTimeout(long timeoutTicks)
        {
            throw new NotImplementedException();
        }

        public ValueTask SendHeaderWithBody(MessageType type,
            ReadOnlyMemory<byte>? messageHeader,
            ReadOnlySequence<byte> body,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }*/
    private NexusDuplexPipe.PipeReaderImpl CreateReader()
    {
        var reader = new NexusDuplexPipe.PipeReaderImpl();

        return reader;
    }

    [Test]
    public async Task ReaderPushesData()
    {
        var tcs = new TaskCompletionSource();
        var simpleData = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var reader = CreateReader();
        reader.BufferData(new ReadOnlySequence<byte>(simpleData));


        _ = Task.Run(async () =>
        {
            var readData = await reader.ReadAsync();
            Assert.AreEqual(10, readData.Buffer.Length);
            tcs.SetResult();
        });

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task WaitsForData()
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

        reader.BufferData(new ReadOnlySequence<byte>(simpleData));

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


        reader.BufferData(new ReadOnlySequence<byte>(simpleData));

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
                reader.BufferData(new ReadOnlySequence<byte>(new byte[] { (byte)i }));
                await bufferSemaphore.WaitAsync();
            }
            
        });

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                //Console.WriteLine("Reader");
                var task = reader.ReadAsync().AsTask();
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
    public async Task CancelPendingReadCancelsReadBeforeRead()
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
    public async Task CancelPendingReadCancelsAllowsReadingAfter()
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
        reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        
        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task CancelPendingReadPreCancelsAllowsReadingAfter()
    {
        var tcs = new TaskCompletionSource();
        var reader = CreateReader();

        reader.CancelPendingRead();
        reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

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
    public async Task ReadIsCanceledByCancellationToken_PriorToRead()
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
    public async Task ReadIsCanceledByCancellationToken_PostRead()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();

        Task.Run(async () =>
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
    public async Task ReadWillContinueAfterCancelByCancellationToken_Pre()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();

        cts.Cancel();
        reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));

        var result = await reader.ReadAsync(cts.Token);
        Assert.IsTrue(result.IsCanceled);

        result = await reader.ReadAsync();
        Assert.IsFalse(result.IsCanceled);
        Assert.AreEqual(10, result.Buffer.Length);
    }

    [Test]
    public async Task ReadWillContinueAfterCancelByCancellationToken_Post()
    {
        var reader = CreateReader();

        var cts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            await Task.Delay(100);
            cts.Cancel();

            reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        });

        var result = await reader.ReadAsync(cts.Token);
        Assert.IsTrue(result.IsCanceled);

        result = await reader.ReadAsync();
        Assert.IsFalse(result.IsCanceled);
        Assert.AreEqual(10, result.Buffer.Length);
    }

    [Test]
    public async Task ReadAdvance()
    {
        var reader = CreateReader();
        for (int i = 0; i < 9000; i++)
        {
            reader.BufferData(new ReadOnlySequence<byte>(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        }
        var result = await reader.ReadAsync();

        var position = result.Buffer.GetPosition(3000 * 16);

        reader.AdvanceTo(position);
    }

}