using NexNet.Diagnostics;
using NexNet.Pipes;
using NUnit.Framework;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// Session and server snapshots.
/// </summary>
internal class SnapshotTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task Snapshot_DescribesTheSession(Type type)
    {
        var server = CreateServer(CreateServerConfig(type), nexus =>
            nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value));
        await server.StartAsync().Timeout(1);
        var (client, _) = CreateClient(CreateClientConfig(type));
        Assert.That(client.GetSnapshot(), Is.Null);
        await client.ConnectAsync().Timeout(1);
        await client.Proxy.ServerTaskValueWithParam(1).Timeout(1);

        var snapshot = client.GetSnapshot()!;
        Assert.That(snapshot.IsServer, Is.False);
        Assert.That(snapshot.Transport, Is.EqualTo(type == Type.Uds ? "uds" : "tcp"));
        Assert.That(snapshot.State, Is.EqualTo(ConnectionState.Connected));
        Assert.That(snapshot.IsEstablished, Is.True);
        Assert.That(snapshot.Connected, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(snapshot.SinceLastSent, Is.Not.Null);
        Assert.That(snapshot.FramesSent, Is.GreaterThanOrEqualTo(2));
        Assert.That(snapshot.Budget.Limit, Is.EqualTo(client.Config.SessionBudget));
        Assert.That(snapshot.Invocations.MaxExecuting, Is.EqualTo(client.Config.MaxConcurrentConnectionInvocations));
        Assert.That(snapshot.Pipes, Is.Empty);
        Assert.That(snapshot.LargeMessages, Is.Empty);

        var serverSnapshot = server.GetSnapshot();
        Assert.That(serverSnapshot.State, Is.EqualTo(NexusServerState.Running));
        Assert.That(serverSnapshot.PendingHandshakes, Is.EqualTo(0));
        Assert.That(serverSnapshot.RateLimiter, Is.Null);
        var session = serverSnapshot.Sessions.Single();
        Assert.That(session.Id, Is.EqualTo(snapshot.Id));
        Assert.That(session.IsServer, Is.True);
        Assert.That(session.Invocations.InboundCredits, Is.EqualTo(server.Config.InvocationCredits));
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
        Assert.That(fromContext.IsServer, Is.True);
        Assert.That(fromContext.Invocations.Executing, Is.EqualTo(1));
        Assert.That(client.GetSnapshot()!.Invocations.AwaitingResults, Is.EqualTo(1));

        release.SetResult();
        await call.Timeout(1);
        Assert.That(client.GetSnapshot()!.Invocations.AwaitingResults, Is.EqualTo(0));
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

        Assert.That(serverPipe!.PartialItem!.Length, Is.EqualTo(prefixLength + itemLength));
        Assert.That(serverPipe.Window, Is.EqualTo(serverConfig.PipeWindow));
        Assert.That(serverPipe.SinceLastData, Is.Not.Null);
        Assert.That(serverPipe.IsReady, Is.True);
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

        Assert.That(clientPipe!.WaitingBytes, Is.EqualTo(serverConfig.PipeWindow));
        Assert.That(clientPipe.SendCredit, Is.LessThanOrEqualTo(0));

        var serverPipe = server.GetSnapshot().Sessions.Single().Pipes.Single();
        Assert.That(serverPipe.Buffered, Is.EqualTo(serverConfig.PipeWindow));
        Assert.That(serverPipe.ReceiveCredit, Is.EqualTo(0));

        release.SetResult();
        _ = writing;
    }
}
