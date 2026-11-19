using NexNet.Diagnostics;
using NexNet.Pipes;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Session and server snapshots.
/// </summary>
[NotInParallel("stress")]
internal class SnapshotTests : BaseTests
{
    [Test]
    [Transports(Type.Uds, Type.Tcp)]
    public async Task Snapshot_DescribesTheSession(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), nexus =>
            nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value));
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        await Assert.That(client.GetSnapshot()).IsNull();
        await client.ConnectAsync().Timeout(1);
        await client.Proxy.ServerTaskValueWithParam(1).Timeout(1);

        var snapshot = client.GetSnapshot()!;
        await Assert.That(snapshot.IsServer).IsFalse();
        await Assert.That(snapshot.Transport).IsEqualTo(type == Type.Uds ? "uds" : "tcp");
        await Assert.That(snapshot.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(snapshot.IsEstablished).IsTrue();
        await Assert.That(snapshot.Connected).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(snapshot.SinceLastSent).IsNotNull();
        await Assert.That(snapshot.FramesSent).IsGreaterThanOrEqualTo(2);
        await Assert.That(snapshot.Budget.Limit).IsEqualTo(client.Config.SessionBudget);
        await Assert.That(snapshot.Invocations.MaxExecuting).IsEqualTo(client.Config.MaxConcurrentConnectionInvocations);
        await Assert.That(snapshot.Pipes).IsEmpty();
        await Assert.That(snapshot.LargeMessages).IsEmpty();

        var serverSnapshot = server.GetSnapshot();
        await Assert.That(serverSnapshot.State).IsEqualTo(NexusServerState.Running);
        await Assert.That(serverSnapshot.PendingHandshakes).IsEqualTo(0);
        await Assert.That(serverSnapshot.RateLimiter).IsNull();
        var session = serverSnapshot.Sessions.Single();
        await Assert.That(session.Id).IsEqualTo(snapshot.Id);
        await Assert.That(session.IsServer).IsTrue();
        await Assert.That(session.Invocations.InboundCredits).IsEqualTo(server.Config.InvocationCredits);
    }

    [Test]
    public async Task Snapshot_ShowsRunningAndAwaitedInvocations()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource<NexusSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(CreateServerConfig(Type.Uds), nexus => nexus.ServerTaskEvent = async n =>
        {
            running.TrySetResult(n.Context.GetSnapshot());
            await release.Task;
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(1);

        var call = client.Proxy.ServerTask().AsTask();
        var fromContext = await running.Task.Timeout(1);
        await Assert.That(fromContext.IsServer).IsTrue();
        await Assert.That(fromContext.Invocations.Executing).IsEqualTo(1);
        await Assert.That(client.GetSnapshot()!.Invocations.AwaitingResults).IsEqualTo(1);

        release.SetResult();
        await call.Timeout(1);
        await Assert.That(client.GetSnapshot()!.Invocations.AwaitingResults).IsEqualTo(0);
    }

    [Test]
    public async Task Snapshot_ShowsPartialChannelItem()
    {
        var serverConfig = CreateServerConfig(Type.Uds);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, pipe) =>
        {
            reading.TrySetResult();
            await foreach (var item in await pipe.GetChannel<byte[]>().GetReaderAsync())
            {
            }
        });
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(1);

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);
        await reading.Task.Timeout(1);

        // Prefix of a 100 KiB item and the first 10 KiB of it; the rest never comes.
        const int itemLength = 100 * 1024;
        var prefix = new byte[5];
        var prefixLength = ChannelFraming.WriteLength(prefix, itemLength);
        await pipe.Output.WriteAsync(prefix.AsMemory(0, prefixLength)).Timeout(1);
        await pipe.Output.WriteAsync(new byte[10 * 1024]).Timeout(1);

        NexusPipeSnapshot? serverPipe = null;
        await MetricsSessionTests.WaitFor(() =>
        {
            serverPipe = server.GetSnapshot().Sessions.Single().Pipes.SingleOrDefault();
            return serverPipe?.PartialItem?.Received == prefixLength + 10 * 1024;
        }).Timeout(2);

        await Assert.That(serverPipe!.PartialItem!.Length).IsEqualTo(prefixLength + itemLength);
        await Assert.That(serverPipe.Window).IsEqualTo(serverConfig.PipeWindow);
        await Assert.That(serverPipe.SinceLastData).IsNotNull();
        await Assert.That(serverPipe.IsReady).IsTrue();
    }

    [Test]
    public async Task Snapshot_ShowsWriterWaitingForCredit()
    {
        // The server never reads, so the writer uses up the window and waits with the rest.
        var serverConfig = CreateServerConfig(Type.Uds);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, _) => await release.Task);
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(Type.Uds));
        await client.ConnectAsync().Timeout(1);

        await using var pipe = client.CreatePipe();
        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        await pipe.ReadyTask.Timeout(1);

        var writing = pipe.Output.WriteAsync(new byte[serverConfig.PipeWindow * 2]).AsTask();
        NexusPipeSnapshot? clientPipe = null;
        await MetricsSessionTests.WaitFor(() =>
        {
            clientPipe = client.GetSnapshot()!.Pipes.SingleOrDefault();
            return clientPipe?.WaitingForCredit != null;
        }).Timeout(2);

        await Assert.That(clientPipe!.WaitingBytes).IsEqualTo(serverConfig.PipeWindow);
        await Assert.That(clientPipe.SendCredit).IsLessThanOrEqualTo(0);

        var serverPipe = server.GetSnapshot().Sessions.Single().Pipes.Single();
        await Assert.That(serverPipe.Buffered).IsEqualTo(serverConfig.PipeWindow);
        await Assert.That(serverPipe.ReceiveCredit).IsEqualTo(0);

        release.SetResult();
        _ = writing;
    }
}