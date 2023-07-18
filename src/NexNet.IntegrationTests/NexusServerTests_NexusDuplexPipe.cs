using System.Buffers;
using System.IO.Pipelines;
using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal class NexusServerTests_NexusDuplexPipe : BasePipeTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeReaderReceivesData(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, result.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await pipe.Output.WriteAsync(Data);
            await Task.Delay(10000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeWriterSendsData(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await pipe.Output.WriteAsync(Data);
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, result.Buffer.ToArray());
            tcs.SetResult();

        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeReaderCompletesUponMethodExit(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
        });
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeWriterCompletesUponMethodExit(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
        });
        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeReaderCompletesUponDisconnection(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await Task.Delay(100);
            await sNexus.Context.DisconnectAsync();
            await Task.Delay(10000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask()
            .Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeWriterCompletesUponDisconnection(Type type)
    {
        var tcsDisconnected = new TaskCompletionSource();
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await tcsDisconnected.Task;
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data);
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await cNexus.Context.DisconnectAsync();
            tcsDisconnected.SetResult();
            await Task.Delay(10000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe).AsTask()
            .Timeout(1);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeReaderCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await pipe.Output.CompleteAsync();
            await Task.Delay(10000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeWriterCompletesUponWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type, true);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await pipe.Output.WriteAsync(Data);
                await Task.Delay(1);
            }

            Assert.IsTrue(result.Value.IsCompleted);
            tcs.SetResult();

        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await pipe.Input.CompleteAsync();
            await Task.Delay(1000000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeWriterRemainsOpenUponOtherWriterCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type, true);

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Input.ReadAsync();
            Assert.IsTrue(result.IsCompleted);

            await pipe.Output.WriteAsync(Data);
            await Task.Delay(10000);
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await pipe.Output.CompleteAsync();

            var result = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, result.Buffer.ToArray());
            tcs.SetResult();
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1000);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task PipeReaderRemainsOpenUponOtherReaderCompletion(Type type)
    {
        var (_, sNexus, _, cNexus, tcs) = await Setup(type);
        var outputComplete = new TaskCompletionSource();

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            await outputComplete.Task.Timeout(1);
            await Task.Delay(50);
            var result = await pipe.Output.WriteAsync(Data);
            Assert.IsTrue(result.IsCompleted);

            await pipe.Input.ReadAsync();
            tcs.SetResult();

            var readResult = await pipe.Input.ReadAsync();
            Assert.AreEqual(Data, readResult.Buffer.ToArray());
            tcs.SetResult();
        };

        var pipe = cNexus.Context.CreatePipe(async pipe =>
        {
            await pipe.Input.CompleteAsync();
            outputComplete.TrySetResult();

            await pipe.Output.WriteAsync(Data);
            await Task.Delay(1000);
        });

        await cNexus.Context.Proxy.ServerTaskValueWithDuplexPipe(pipe);

        await tcs.Task.Timeout(1);
    }

}
