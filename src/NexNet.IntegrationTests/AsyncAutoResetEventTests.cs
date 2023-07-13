using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
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

internal class AsyncAutoResetEventTests
{

    [Test]
    public async Task FiresImmediately()
    {
        var resetEvent = new AsyncAutoResetEvent(true);
        await resetEvent.WaitAsync().AsTask().Timeout(1);
    }

    [Test]
    public async Task Waits()
    {
        var resetEvent = new AsyncAutoResetEvent(false);
        await resetEvent.WaitAsync().AsTask().AssertTimeout(0.1);
    }

    [Test]
    public async Task Resets()
    {
        var tcs = new TaskCompletionSource();
        var resetEvent = new AsyncAutoResetEvent(false);

        _ = Task.Run(async () =>
        {
            await resetEvent.WaitAsync().AsTask().Timeout(1);
            await resetEvent.WaitAsync().AsTask().AssertTimeout(0.1);
            tcs.SetResult();
        });

        resetEvent.Set();

        await tcs.Task.Timeout(1);
    }

    [Test]
    public async Task ResetsMultipleTimes()
    {
        var tcs = new TaskCompletionSource();
        var resetEvent = new AsyncAutoResetEvent(false);
        var resetSemaphore = new SemaphoreSlim(0, 1);
        bool released = false;

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                //Assert.IsFalse(released);
                await resetEvent.WaitAsync();
                //Assert.IsTrue(released);
                released = false;
                resetSemaphore.Release();
            }

            tcs.SetResult();
        });

        _ = Task.Run(async () =>
        {

            for (int i = 0; i < 10; i++)
            {
                released = true;

                await Task.Delay(10);
                resetEvent.Set();
                await resetSemaphore.WaitAsync();
            }

        });


        await tcs.Task.Timeout(2);
    }

    [Test]
    public async Task ResetsMultipleTimesImmediate()
    {
        var tcs = new TaskCompletionSource();
        var resetEvent = new AsyncAutoResetEvent(false);
        var resetSemaphore = new SemaphoreSlim(0, 1);
        bool released = false;

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 500; i++)
            {
                //Assert.IsFalse(released);
                await resetEvent.WaitAsync();
                //Assert.IsTrue(released);
                released = false;
                resetSemaphore.Release();
            }

            tcs.SetResult();
        });

        _ = Task.Run(async () =>
        {

            for (int i = 0; i < 500; i++)
            {
                released = true;
                resetEvent.Set();
                await resetSemaphore.WaitAsync();
            }

        });


        await tcs.Task.Timeout(2);
    }



}
