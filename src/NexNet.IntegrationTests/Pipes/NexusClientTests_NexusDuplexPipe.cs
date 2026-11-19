using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Interop;
using NexNet.Interop.Hosts;
using NexNet.Interop.InProc;
using NexNet.InteropServer;
using NexNet.Messages;
using NexNet.Pipes;
using NexNet.Transports;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// Pipes between the client under test and the server, mostly opened by the server (the client's registrant role).
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class NexusClientTests_NexusDuplexPipe(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    private const string NoPipeHandler =
        "no host handler can write to a pipe the server opened or report what the client's pipe saw (follow-up card)";

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReceivesFullFrames(ClientTarget target)
    {
        // 4 MiB in maximum-size frames: exceeds the transport's receive pause threshold, the pipe's high watermark
        // and its cutoff, so every one of them has to resume.
        const int total = 4 * 1024 * 1024;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            s => s.ClientTaskValueWithDuplexPipe.Named("pipe.readExact", ReadExact(total)));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.Output.WriteAsync(PipePattern.Create(total, 1)).AsTask().Timeout(T(5));

            // The client completes the pipe once it has read every byte.
            await pipe.CompleteTask.Timeout(T(10));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReaderReceivesDataMultipleTimes(ClientTarget target)
    {
        const int iterations = 21;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, s => s.ClientTaskValueWithDuplexPipe.Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = await OpenToClientAsync(sNexus);
            await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await pipe.Output.CompleteAsync().AsTask().Timeout(T(1));

            // The client read the data: it wrote it back.
            await Assert.That(await ReadToEndAsync(pipe.Input)).IsEquivalentTo(Data, CollectionOrdering.Matching);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReaderReceivesDataMultipleTimesWithLargeData(ClientTarget target)
    {
        const int length = 1024 * 32;
        // 1000 round trips, each step bounded: under a loaded parallel run one step can stall for over a second, so
        // the bounds catch a hang rather than a slow step.
        const int iterations = 1000;
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            s => s.ClientTaskValueWithDuplexPipe.Named("pipe.readExact", ReadExact(length)));
        var sNexus = await ServerNexusAsync();
        var largeData = new byte[length];

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = await OpenToClientAsync(sNexus);
            await pipe.Output.WriteAsync(largeData).AsTask().Timeout(T(5));

            // The client completes the pipe once it has read every byte.
            await pipe.CompleteTask.Timeout(T(5));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReaderReceivesData(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, s => s.ClientTaskValueWithDuplexPipe.Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await pipe.Output.CompleteAsync().AsTask().Timeout(T(1));
            await Assert.That(await ReadToEndAsync(pipe.Input)).IsEquivalentTo(Data, CollectionOrdering.Matching);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeWriterSendsData(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, s => s.ClientTaskValueWithDuplexPipe.Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            // The server's side stays open: the client writes while the pipe is open both ways.
            await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await Assert.That(await ReadExactAsync(pipe.Input, Data.Length)).IsEquivalentTo(Data, CollectionOrdering.Matching);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReaderCompletesUponPipeCompleteAsync(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            s => ThenReport(s.ClientTaskValueWithDuplexPipe.Named("pipe.drain"), 1));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.CompleteAsync().AsTask().Timeout(T(1));

            // pipe.drain returns only once the client's reader completes.
            await WaitForReportAsync(1);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeWriterCompletesUponCompleteAsync(ClientTarget target)
    {
        RequireInProc(target, NoPipeHandler);
        var tcs = Signal();
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        InProc(client).Nexus.ClientTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
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
        };
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.CompleteAsync().AsTask().Timeout(T(1));
            await tcs.Task.Timeout(T(3));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReaderCompletesUponDisconnection(ClientTarget target)
    {
        RequireInProc(target, NoPipeHandler);
        var tcs = Signal();
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        InProc(client).Nexus.ClientTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            var result = await pipe.Input.ReadAsync().AsTask().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };
        var sNexus = await ServerNexusAsync();

        await using (await OpenToClientAsync(sNexus))
        {
            await client.DisconnectAsync().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeWriterCompletesUponDisconnection(ClientTarget target)
    {
        RequireInProc(target, NoPipeHandler);
        var tcs = Signal();
        var tcsDisconnected = Signal();
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        InProc(client).Nexus.ClientTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            await tcsDisconnected.Task.Timeout(T(1));
            await Task.Delay(150);
            var result = await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await Assert.That(result.IsCompleted).IsTrue();
            tcs.SetResult();
        };
        var sNexus = await ServerNexusAsync();

        await using (await OpenToClientAsync(sNexus))
        {
            await client.DisconnectAsync().Timeout(T(1));
            tcsDisconnected.SetResult();
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReaderCompletesUponWriterCompletion(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            s => ThenReport(s.ClientTaskValueWithDuplexPipe.Named("pipe.drain"), 1));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.Output.CompleteAsync().AsTask().Timeout(T(1));

            // pipe.drain returns only once the client's reader completes.
            await WaitForReportAsync(1);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeWriterCompletesUponWriterCompletion(ClientTarget target)
    {
        RequireInProc(target, NoPipeHandler);
        var tcs = Signal();
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server);
        InProc(client).Nexus.ClientTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            FlushResult? result = null;
            while (result == null || !result.Value.IsCompleted)
            {
                result = await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
                await Task.Delay(1);
            }

            await Assert.That(result.Value.IsCompleted).IsTrue();
            tcs.SetResult();
        };
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.Input.CompleteAsync().AsTask().Timeout(T(1));
            await tcs.Task.Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeWriterRemainsOpenUponOtherWriterCompletion(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, s => s.ClientTaskValueWithDuplexPipe.Named("pipe.echo"));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await pipe.Output.CompleteAsync().AsTask().Timeout(T(1));

            // The server's writer is complete; the client's still delivers.
            await Assert.That(await ReadToEndAsync(pipe.Input)).IsEquivalentTo(Data, CollectionOrdering.Matching);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeReaderRemainsOpenUponOtherReaderCompletion(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            s => ThenReport(s.ClientTaskValueWithDuplexPipe.Named("pipe.readExact", ReadExact(Data.Length)), 1));
        var sNexus = await ServerNexusAsync();

        await using (var pipe = await OpenToClientAsync(sNexus))
        {
            await pipe.Input.CompleteAsync().AsTask().Timeout(T(1));
            await Task.Delay(50);

            // The server's reader is complete; the client's still receives.
            await pipe.Output.WriteAsync(Data).AsTask().Timeout(T(1));
            await WaitForReportAsync(1);
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeNotifiesWhenReady(ClientTarget target)
    {
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server, s => s.ClientTaskValueWithDuplexPipe.AwaitSignal("release"));
        var sNexus = await ServerNexusAsync();

        // OpenToClientAsync waits for the pipe to be ready while the client's method is still running.
        await using (await OpenToClientAsync(sNexus))
            await client.SignalAsync("release");

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task Client_PipeReadyCancelsOnDisconnection(ClientTarget target)
        => PipeCancelsOnDisconnection(target, pipe => pipe.WhenReadyAsync());

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public Task Client_PipeCompleteCancelsOnDisconnection(ClientTarget target)
        => PipeCancelsOnDisconnection(target, pipe => pipe.WhenCompletedAsync());

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_PipeNotifiesWhenComplete(ClientTarget target)
    {
        var completedInvocation = false;
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, _) =>
        {
            await Task.Delay(100);
            completedInvocation = true;
        });
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenReadyAsync().AsTask().Timeout(T(1));
            await pipe.WhenCompletedAsync().AsTask().Timeout(T(1));
        }

        await Assert.That(completedInvocation).IsTrue();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_ThrowsWhenPassingPipeFromWrongNexus(ClientTarget target)
    {
        // C#-runtime specific: only a C# test can hand the client a pipe object of another nexus.
        RequireInProc(target, "the test passes a C# server pipe to the client's proxy");
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = (_, _) => ValueTask.CompletedTask);
        await using var client = await ConnectAsync(target, server);
        var sNexus = await ServerNexusAsync();

        await using (var pipe = sNexus.Context.CreatePipe())
        {
            await AssertThrows<InvalidOperationException>(() =>
                InProc(client).Client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1))).Timeout(T(1));
        }

        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.TcpTls, ClientTransport.WebSocket, ClientTransport.HttpSocket, ClientTransport.WebSocketTls], CSharpTransports = [ClientTransport.Quic])]
    public async Task Client_ThrowsWhenPassingUsedPipe(ClientTarget target)
    {
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskValueWithDuplexPipeEvent = (_, _) => ValueTask.CompletedTask);
        await using var client = await ConnectAsync(target, server);

        await using (var pipe = await client.CreatePipeAsync())
        {
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await pipe.WhenCompletedAsync().AsTask().Timeout(T(1));

            await AssertThrows<InvalidOperationException>(() =>
                Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1))).Timeout(T(1));
        }

        await EndAsync(client);
    }

    /// <summary>
    /// The server holds every frame from the client once the client's pipe invocation arrives, and the client
    /// disconnects meanwhile: the pipe never becomes ready and <paramref name="wait"/> throws a cancellation.
    /// </summary>
    private async Task PipeCancelsOnDisconnection(ClientTarget target, Func<IRemotePipe, ValueTask> wait)
    {
        var release = Signal();
        var clientReady = new TaskCompletionSource<IClientUnderTest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await StartServerAsync(configure: config => ServerTaps.OnReceive(config, async (session, frame) =>
        {
            if (frame.Type != MessageType.Invocation)
                return;

            var connected = await clientReady.Task;
            _ = connected.DisconnectAsync();
            await release.Task;
        }));
        await using var client = await ConnectAsync(target, server);
        clientReady.SetResult(client);

        try
        {
            await using var pipe = await client.CreatePipeAsync();
            await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
            await AssertThrows<TaskCanceledException>(async () => await wait(pipe)).Timeout(T(2));
        }
        finally
        {
            release.TrySetResult();
        }

        await EndAsync(client);
    }
}
