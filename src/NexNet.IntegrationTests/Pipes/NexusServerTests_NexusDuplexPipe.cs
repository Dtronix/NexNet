using System.Buffers;
using System.IO.Pipelines;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Messages;
using NexNet.Pipes;

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Pipes between the client under test and the server, mostly opened by the client and handled by the server.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusServerTests_NexusDuplexPipe(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeReaderReceivesDataMultipleTimes(ClientTarget target)
    {
        var tcs = Signal();
        var count = 0;
        const int iterations = 1000;
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().AsTask().Timeout(T(1));

            // If the connection is still alive, the buffer should contain the data.
            if (!result.IsCompleted)
                await Assert.That(result.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = await client.CreatePipeAsync();
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.WriteAsync(Data).AsTask().Timeout(T(1));
        }

        await tcs.Task.Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeReaderReceivesDataMultipleTimesWithLargeData(ClientTarget target)
    {
        var tcs = Signal();
        var count = 0;
        const int length = 1024 * 32;
        const int iterations = 100;
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            bool complete;
            do
            {
                var result = await pipe.Input.ReadAsync().AsTask().Timeout(T(1));
                pipe.Input.AdvanceTo(result.Buffer.End);
                complete = result.IsCompleted;
            } while (!complete);

            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = await client.CreatePipeAsync();
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.WritePatternAsync(i, length).AsTask().Timeout(T(1));
            await pipe.CompleteAsync().AsTask().Timeout(T(1));
        }

        await tcs.Task.Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeReaderCreatesAndDestroysPipeMultipleTimes(ClientTarget target)
    {
        var tcs = Signal();
        var count = 0;
        const int iterations = 50;
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = (_, _) =>
        {
            if (Interlocked.Increment(ref count) == iterations)
                tcs.SetResult();

            return ValueTask.CompletedTask;
        });
        await using var client = await ConnectAsync(target, server);

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = await client.CreatePipeAsync();
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
        }

        await tcs.Task.Timeout(T(2));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeReaderReceivesData(ClientTarget target)
    {
        var tcs = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await Assert.That(await ReadExactAsync(pipe.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.WriteAsync(Data).AsTask().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeWriterSendsData(ClientTarget target)
    {
        var release = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await release.Task;
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            var read = await pipe.ReadAsync(Data.Length, Data.Length).AsTask().Timeout(T(1));
            await Assert.That(read.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
            release.SetResult();
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeReaderCompletesUponCompleteAsync(ClientTarget target)
    {
        var tcs = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().AsTask().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.CompleteAsync().AsTask().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeWriterCompletesUponCompleteAsync(ClientTarget target)
    {
        var tcs = Signal();
        var completedTcs = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            // Wait for the client to close the pipe.
            await completedTcs.Task.Timeout(T(1));

            FlushResult result = default;
            for (var i = 0; i < 20; i++)
            {
                result = await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
                if (result.IsCompleted)
                    break;

                await Task.Delay(100);
            }

            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        var pipe = await client.CreatePipeAsync();
        await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.WhenReadyAsync().AsTask().Timeout(T(1));

        // Closing the pipe completes both of the client's sides.
        await pipe.DisposeAsync().AsTask().Timeout(T(1));
        completedTcs.SetResult();

        await tcs.Task.Timeout(T(3));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeReaderCompletesUponDisconnection(ClientTarget target)
    {
        var tcs = Signal();
        var reading = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            reading.TrySetResult();
            var result = await pipe.Input.ReadAsync();
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await reading.Task.Timeout(T(1));
            await sNexus.Context.DisconnectAsync().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeWriterCompletesUponDisconnection(ClientTarget target)
    {
        var tcs = Signal();
        var tcsDisconnected = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await tcsDisconnected.Task.Timeout(T(1));
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await client.DisconnectAsync().Timeout(T(1));
            tcsDisconnected.SetResult();
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeReaderCompletesUponWriterCompletion(ClientTarget target)
    {
        var tcs = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().AsTask().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.CompleteAsync().AsTask().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic], Requires = ClientCapabilities.PipeInputCompletion)]
    public async Task Server_PipeWriterCompletesUponWriterCompletion(ClientTarget target)
    {
        var tcs = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
                await Task.Delay(1);
            }

            await Assert.That(result.Value.IsCompleted).IsTrue();
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.CompleteInputAsync().AsTask().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeWriterRemainsOpenUponOtherWriterCompletion(ClientTarget target)
    {
        var release = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().AsTask().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();

            await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await release.Task;
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.CompleteAsync().AsTask().Timeout(T(1));

            var read = await pipe.ReadAsync(Data.Length, Data.Length).AsTask().Timeout(T(1));
            await Assert.That(read.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
            release.SetResult();
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic], Requires = ClientCapabilities.PipeInputCompletion)]
    public async Task Server_PipeReaderRemainsOpenUponOtherReaderCompletion(ClientTarget target)
    {
        var tcs = Signal();
        var outputComplete = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await outputComplete.Task.Timeout(T(1));
            await Task.Delay(50);
            var result = await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();

            var readResult = await pipe.Input.ReadAsync().AsTask().Timeout(T(1));
            await Assert.That(readResult.Buffer.ToArray()).IsEquivalentTo(Data, CollectionOrdering.Matching);
            tcs.SetResult();
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.CompleteInputAsync().AsTask().Timeout(T(1));
            outputComplete.TrySetResult();

            await pipe.WriteAsync(Data).AsTask().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeNotifiesWhenReady(ClientTarget target)
    {
        var release = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, _) => await release.Task);
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            release.SetResult();
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task Server_PipeReadyCancelsOnDisconnection(ClientTarget target)
        => ServerPipeCancelsOnDisconnection(target, pipe => pipe.ReadyTask);

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task Server_PipeCompleteCancelsOnDisconnection(ClientTarget target)
        => ServerPipeCancelsOnDisconnection(target, pipe => pipe.CompleteTask);

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_PipeNotifiesWhenComplete(ClientTarget target)
    {
        await using var server = await StartServerAsync();

        // The client's method reports to the server before it returns, which completes the pipe.
        await using var client = await ConnectAsync(target, server,
            s => ThenReport(s.ClientTaskValueWithDuplexPipe.Delay(100), 1));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.CompleteTask.Timeout(T(1));
            await WaitForReportAsync(1, 0);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task Server_PipesThrowWhenInvokingOnMultipleConnections(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.All.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Others.ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Clients([sNexus.Context.Id]).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Group("").ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await AssertThrows<InvalidOperationException>(async () =>
        {
            await sNexus.Context.Clients.Groups([""]).ClientTaskValueWithDuplexPipe(sNexus.Context.CreatePipe()).Timeout(T(1));
        }).Timeout(T(1));

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds])]
    public async Task Server_PipesAllowInvocationOnSingleConnections(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();

        await using var first = sNexus.Context.CreatePipe();
        await using var second = sNexus.Context.CreatePipe();
        await sNexus.Context.Clients.Client(sNexus.Context.Id).ClientTaskValueWithDuplexPipe(first).Timeout(T(1));
        await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(second).Timeout(T(1));

        // The client's unscripted method returns at once, which completes each pipe.
        await first.CompleteTask.Timeout(T(1));
        await second.CompleteTask.Timeout(T(1));
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Languages = [ClientLanguages.CSharp], Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_ThrowsWhenPassingPipeFromWrongNexus(ClientTarget target)
    {
        // C# rows only: the test checks the C# server's proxy, handing it a pipe object of the C# client's nexus. The
        // client under test plays no part, and no other language can hand the server a pipe object.
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();

        await using (var pipe = InProc(client).Client.CreatePipe())
        {
            await AssertThrows<InvalidOperationException>(() =>
                sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1))).Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Server_ThrowsWhenPassingUsedPipe(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();

        await using (var pipe = sNexus.Context.CreatePipe())
        {
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1));

            // The client's unscripted method returns at once, which completes the pipe.
            await pipe.CompleteTask.Timeout(T(1));

            await AssertThrows<InvalidOperationException>(() =>
                sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1))).Timeout(T(1));
        }

        await EndAsync(client);
    }

    /// <summary>
    /// The server opens a pipe to the client, holds the client's pipe-ready answer and disconnects, so the pipe never
    /// becomes ready on the server and <paramref name="wait"/> throws a cancellation.
    /// </summary>
    private async Task ServerPipeCancelsOnDisconnection(ClientTarget target, Func<IRentedNexusDuplexPipe, Task> wait)
    {
        var release = Signal();
        var nexusReady = new TaskCompletionSource<ServerNexus>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(configure: config => ServerTaps.OnReceive(config, async (session, frame) =>
        {
            if (frame.Type != MessageType.DuplexPipeUpdateState)
                return;

            var nexus = await nexusReady.Task;
            _ = nexus.Context.DisconnectAsync();
            await release.Task;
        }));
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();
        nexusReady.SetResult(sNexus);

        try
        {
            await using var pipe = sNexus.Context.CreatePipe();
            await sNexus.Context.Clients.Caller.ClientTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await AssertThrows<TaskCanceledException>(async () => await wait(pipe)).Timeout(T(1));
        }
        finally
        {
            release.TrySetResult();
        }

        await EndAsync(client);
    }
}
