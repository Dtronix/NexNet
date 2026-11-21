using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Pipes;

/// <summary>
/// A pipe that closes while its writer is still sending must end only that pipe, never the session.
/// </summary>
internal class PipeCloseWriteRaceTests : BaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task ServerReleasesPipeWhileWriting_SessionSurvives(Type type)
    {
        // The server method returns (releasing its pipe) while a background task is still writing to it, so the
        // pipe is closed and disposed in the middle of a flush. Repeated to hit every interleaving.
        const int iterations = 100;
        var writeErrors = 0;
        Exception? lastError = null;
        var server = CreateServer(CreateServerConfig(type), nexus =>
        {
            nexus.ServerTaskValueWithDuplexPipeEvent = async (_nexus, pipe) =>
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var data = new byte[96 * 1024];
                        while (true)
                        {
                            var flush = pipe.Output.WriteAsync(data);
                            started.TrySetResult();
                            if ((await flush).IsCompleted)
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref writeErrors);
                        lastError = e;
                    }
                    finally
                    {
                        started.TrySetResult();
                    }
                });

                await started.Task;
                await Task.Delay(Random.Shared.Next(0, 3));
            };
            nexus.ServerTaskValueEvent = _ => new ValueTask<int>(1);
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await client.ConnectAsync().Timeout(1);

        for (var i = 0; i < iterations; i++)
        {
            await using var pipe = client.CreatePipe();
            await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
            await pipe.ReadyTask.Timeout(1);

            // Read until the server's side closes.
            while (true)
            {
                var read = await pipe.Input.ReadAsync().AsTask().Timeout(5);
                pipe.Input.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted || read.IsCanceled)
                    break;
            }

            await Assert.That(client.State).IsEqualTo(ConnectionState.Connected)
                .Because($"iteration {i}: the session must survive a pipe closing mid-write");
        }

        await Assert.That(await client.Proxy.ServerTaskValue().Timeout(1)).IsEqualTo(1);
        await Assert.That(writeErrors).IsZero().Because(lastError?.ToString() ?? "");
    }
}
