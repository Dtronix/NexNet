using NexNet.Interop;
using NexNet.Interop.Hosts;
using static NexNet.IntegrationTests.Metrics.ClientDiagnostics;

#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests.Metrics;

/// <summary>
/// The client's session snapshot (C# <c>NexusClient.GetSnapshot()</c>, Java <c>Diagnostics.snapshot</c>, JS
/// <c>getSnapshot()</c>) on every row of the client matrix, read through <see cref="IClientUnderTest.GetSnapshotAsync"/>.
/// The server side of each is covered by <see cref="SnapshotTests"/>.
/// </summary>
[ClassDataSource<LanguageHostPool>(Shared = SharedType.PerTestSession)]
internal class ClientSnapshotTests(LanguageHostPool hosts) : MatrixTestBase(hosts)
{
    [Test]
    [ClientMatrix(Transports = [ClientTransport.Uds, ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Snapshot_DescribesTheSession(ClientTarget target)
    {
        const int budget = 32 * 1024 * 1024;
        await using var server = await StartServerAsync(nexus =>
            nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value));
        await using var client = await CreateClientAsync(target, server, spec =>
        {
            spec.SessionBudget = budget;
            spec.MaxConcurrentConnectionInvocations = 3;
        });
        await Assert.That(await client.GetSnapshotAsync().Timeout(T(1))).IsNull().Because("No session before the connect.");

        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));
        await Assert.That(await Proxy(client).ServerTaskValueWithParam(1).Timeout(T(1))).IsEqualTo(1);

        var snapshot = (await client.GetSnapshotAsync().Timeout(T(1)))!;
        await Assert.That(snapshot).IsNotNull();
        await Assert.That(snapshot.IsServer).IsFalse();
        await Assert.That(snapshot.Transport).IsEqualTo(TransportTag(target.Transport));
        await Assert.That(snapshot.Endpoint).IsNull();
        await Assert.That(snapshot.IsSecure).IsEqualTo(await client.GetIsSecureAsync());
        await Assert.That(snapshot.State).IsEqualTo(ConnectionState.Connected);
        await Assert.That(snapshot.IsEstablished).IsTrue();
        await Assert.That(snapshot.PingInterval).IsNotNull();
        await Assert.That(snapshot.Connected).IsGreaterThanOrEqualTo(TimeSpan.Zero);
        await Assert.That(snapshot.SinceLastSent).IsNotNull();
        await Assert.That(snapshot.FramesSent).IsGreaterThanOrEqualTo(2);
        await Assert.That(snapshot.BytesSent).IsGreaterThan(snapshot.FramesSent);
        await Assert.That(snapshot.FramesReceived).IsGreaterThanOrEqualTo(2);
        await Assert.That(snapshot.Budget.Limit).IsEqualTo(budget);
        await Assert.That(snapshot.Invocations.MaxExecuting).IsEqualTo(3);
        await Assert.That(snapshot.Invocations.AwaitingResults).IsEqualTo(0);
        await Assert.That(snapshot.Pipes).IsEmpty();
        await Assert.That(snapshot.LargeMessages).IsEmpty();

        // The same session as the server's.
        var session = server.Server.GetSnapshot().Sessions.Single();
        await Assert.That(session.Id).IsEqualTo(snapshot.Id);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Snapshot_ShowsAwaitedInvocations(ClientTarget target)
    {
        var release = Signal();
        var running = Signal();
        await using var server = await StartServerAsync(nexus => nexus.ServerTaskEvent = async _ =>
        {
            running.TrySetResult();
            await release.Task;
        });
        await using var client = await ConnectAsync(target, server);

        var call = Proxy(client).ServerTask();
        await running.Task.Timeout(T(1));
        var snapshot = await SnapshotUntil(client, s => s.Invocations.AwaitingResults == 1, T(1));
        await Assert.That(snapshot!.Invocations.AwaitingResults).IsEqualTo(1);

        release.SetResult();
        await call.Timeout(T(1));
        snapshot = await SnapshotUntil(client, s => s.Invocations.AwaitingResults == 0, T(1));
        await Assert.That(snapshot!.Invocations.AwaitingResults).IsEqualTo(0);
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Snapshot_ShowsQueuedInvocationsAndTheOldestWait(ClientTarget target)
    {
        // One execution slot on the client: the first call from the server holds it until the signal, the second
        // queues behind it.
        await using var server = await StartServerAsync();
        await using var client = await ConnectAsync(target, server,
            script => script.ClientTaskWithParam.AwaitSignal("release"),
            spec => spec.MaxConcurrentConnectionInvocations = 1);

        var snapshot = (await client.GetSnapshotAsync().Timeout(T(1)))!;
        await Assert.That(snapshot.Invocations.Queued).IsEqualTo(0);
        await Assert.That(snapshot.Invocations.OldestQueuedWait).IsNull();

        Task first, second;
        using (var context = server.Server.ContextProvider.Rent())
        {
            first = context.Proxy.All.ClientTaskWithParam(1).AsTask();
            second = context.Proxy.All.ClientTaskWithParam(2).AsTask();
            await Task.WhenAll(first, second).Timeout(T(1));
        }

        snapshot = await SnapshotUntil(client, s => s.Invocations.Executing == 1 && s.Invocations.Queued == 1, T(2));
        await Assert.That(snapshot!.Invocations.Executing).IsEqualTo(1);
        await Assert.That(snapshot.Invocations.Queued).IsEqualTo(1);
        await Assert.That(snapshot.Invocations.OldestQueuedWait).IsNotNull();

        // Later, the same call has waited longer.
        await Task.Delay(100);
        var later = (await client.GetSnapshotAsync().Timeout(T(1)))!;
        await Assert.That(later.Invocations.OldestQueuedWait!.Value).IsGreaterThan(snapshot.Invocations.OldestQueuedWait!.Value);

        await client.SignalAsync("release").Timeout(T(1));
        snapshot = await SnapshotUntil(client, s => s.Invocations.Executing == 0 && s.Invocations.Queued == 0, T(2));
        await Assert.That(snapshot!.Invocations.Queued).IsEqualTo(0);
        await Assert.That(snapshot.Invocations.OldestQueuedWait).IsNull();
        await EndAsync(client);
    }

    [Test]
    [ClientMatrix(Transports = [ClientTransport.Tcp, ClientTransport.WebSocket])]
    public async Task Snapshot_ShowsWriterWaitingForCredit(ClientTarget target)
    {
        // The server never reads, so the client's writer uses up the server's window and waits with the rest.
        var window = 0;
        var release = Signal();
        await using var server = await StartServerAsync(
            nexus => nexus.ServerTaskValueWithDuplexPipeEvent = async (_, _) => await release.Task,
            config => window = config.PipeWindow);
        await using var client = await ConnectAsync(target, server);

        var pipe = await client.CreatePipeAsync();
        await Proxy(client).ServerTaskValueWithDuplexPipe(pipe).Timeout(T(1));
        await pipe.WhenReadyAsync().AsTask().Timeout(T(1));

        var writing = pipe.WriteAsync(new byte[window * 2]).AsTask();
        var snapshot = await SnapshotUntil(client, s => s.Pipes.SingleOrDefault() is { WaitingForCredit: not null } p && p.WaitingBytes == window, T(2));
        var clientPipe = snapshot!.Pipes.Single();
        await Assert.That(clientPipe.WaitingForCredit).IsNotNull();
        await Assert.That(clientPipe.WaitingBytes).IsEqualTo(window);
        await Assert.That(clientPipe.SendCredit).IsLessThanOrEqualTo(0);
        await Assert.That(clientPipe.IsReady).IsTrue();

        // Ending the server's method closes the pipe, which ends the blocked write.
        release.SetResult();
        await pipe.DisposeAsync().AsTask().Timeout(T(1));
        try
        {
            await writing.Timeout(T(1));
        }
        catch (InvalidOperationException)
        {
            // The pipe completed before the rest was sent.
        }

        snapshot = await SnapshotUntil(client, s => s.Pipes.Count == 0, T(2));
        await Assert.That(snapshot!.Pipes).IsEmpty();
        await EndAsync(client);
    }
}
