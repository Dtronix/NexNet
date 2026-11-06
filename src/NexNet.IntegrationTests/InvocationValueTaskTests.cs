using NexNet.IntegrationTests.TestInterfaces;
using NUnit.Framework;

#pragma warning disable VSTHRD200
#pragma warning disable VSTHRD002

namespace NexNet.IntegrationTests;

/// <summary>
/// The <see cref="ValueTask"/> a proxy call returns: backed by the pooled invocation state, consumed once.
/// </summary>
internal class InvocationValueTaskTests : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task BlockingOnAnUnfinishedCall_WaitsForTheResult(Type type)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskValueEvent = async _ =>
            {
                await release.Task;
                return 5;
            };
            nexus.ServerTaskEvent = async _ => await release.Task;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var withResult = Task.Run(() => client.Proxy.ServerTaskValue().GetAwaiter().GetResult());
        var withoutResult = Task.Run(() => client.Proxy.ServerTask().GetAwaiter().GetResult());
        await Task.Delay(100);
        Assert.That(withResult.IsCompleted || withoutResult.IsCompleted, Is.False);

        release.SetResult();
        Assert.That(await withResult.Timeout(1), Is.EqualTo(5));
        await withoutResult.Timeout(1);
    }

    [TestCase(Type.Uds)]
    public async Task ConsumingACallTwice_Throws(Type type)
    {
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => ValueTask.FromResult(5);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var call = client.Proxy.ServerTaskValue();
        Assert.That(await call, Is.EqualTo(5));

        // The state behind it has been returned to its pool; reading it again must not observe another call.
        Assert.Throws<InvalidOperationException>(() => call.GetAwaiter().GetResult());
        Assert.That(await client.Proxy.ServerTaskValue().AsTask().Timeout(1), Is.EqualTo(5));
    }

    [TestCase(Type.Uds)]
    public async Task AwaitWithoutConfigureAwait_ResumesOnTheCallersContext(Type type)
    {
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus =>
        {
            nexus.ServerTaskValueEvent = async _ =>
            {
                await Task.Delay(20);
                return 5;
            };
            nexus.ServerTaskEvent = _ => new ValueTask(Task.Delay(20));
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var context = new CountingContext();
        await Task.Run(async () =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Assert.That(await client.Proxy.ServerTaskValue(), Is.EqualTo(5));
            Assert.That(context.Posts, Is.EqualTo(1));

            // The base context resumes on the thread pool without itself installed.
            SynchronizationContext.SetSynchronizationContext(context);
            await client.Proxy.ServerTask();
            Assert.That(context.Posts, Is.EqualTo(2));
        }).Timeout(1);
    }

    [TestCase(Type.Uds)]
    public async Task ManySequentialCalls_ReuseStatesWithoutCrossTalk(Type type)
    {
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueWithParamEvent = (_, value) => ValueTask.FromResult(value);

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var cts = new CancellationTokenSource();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                var value = worker * 1000 + i;
                Assert.That(await client.Proxy.ServerTaskValueWithParam(value), Is.EqualTo(value));
            }
        }))).Timeout(10);
    }

    // ----- Calls whose send does not complete at once: the third call waits for invocation credit -----

    private sealed class CreditWait : IAsyncDisposable
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NexusServer<ServerNexus, ServerNexus.ClientProxy> Server = null!;
        public NexusClient<ClientNexus, ClientNexus.ServerProxy> Client = null!;
        public Task<int> First = null!;
        public Task<int> Second = null!;
        public int Received;

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await Task.WhenAll(First, Second).Timeout(1);
        }
    }

    // One credit and one execution slot: the first call runs, the second takes the returned credit and waits in the
    // server's queue, and any further call waits for credit, its send not completing.
    private async Task<CreditWait> StartCreditWait(Type type)
    {
        var wait = new CreditWait();
        var serverConfig = CreateServerConfig(type);
        serverConfig.InvocationCredits = 1;
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        wait.Server = CreateServer(serverConfig, nexus => nexus.ServerTaskValueWithCancellationEvent = async (_, _) =>
        {
            Interlocked.Increment(ref wait.Received);
            await wait.Release.Task;
            return 1;
        });
        await wait.Server.StartAsync().Timeout(1);
        (wait.Client, _) = CreateClient(CreateClientConfig(type));
        await wait.Client.ConnectAsync().Timeout(1);

        wait.First = wait.Client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        wait.Second = wait.Client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask();
        await Task.Delay(100);
        return wait;
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task BlockingOnACallWaitingToBeSent_WaitsForTheResult(Type type)
    {
        await using var wait = await StartCreditWait(type);

        var call = wait.Client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
        Assert.That(call.IsCompleted, Is.False);
        var blocked = Task.Run(() => call.GetAwaiter().GetResult());
        await Task.Delay(50);
        Assert.That(blocked.IsCompleted, Is.False);

        wait.Release.SetResult();
        Assert.That(await blocked.Timeout(1), Is.EqualTo(1));
        Assert.That(await wait.Client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask().Timeout(1),
            Is.EqualTo(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task BlockingOnACallCanceledBeforeItIsSent_ReturnsTheDefault(Type type)
    {
        await using var wait = await StartCreditWait(type);

        using var cts = new CancellationTokenSource();
        var call = wait.Client.Proxy.ServerTaskValueWithCancellation(cts.Token);
        var blocked = Task.Run(() => call.GetAwaiter().GetResult());
        await Task.Delay(50);
        cts.Cancel();

        // Like any call canceled before it is sent, it returns the default value; it was never sent.
        Assert.That(await blocked.Timeout(1), Is.Zero);
        wait.Release.SetResult();
        await Task.WhenAll(wait.First, wait.Second).Timeout(1);
        Assert.That(wait.Received, Is.EqualTo(2));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task BlockingOnACallWhoseSessionEndsBeforeItIsSent_Throws(Type type)
    {
        await using var wait = await StartCreditWait(type);

        var call = wait.Client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
        var blocked = Task.Run(() => call.GetAwaiter().GetResult());
        await Task.Delay(50);
        Assert.That(blocked.IsCompleted, Is.False);

        await wait.Client.DisconnectAsync().Timeout(1);
        Assert.That(async () => await blocked.Timeout(1), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(async () => await wait.First.Timeout(1), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(async () => await wait.Second.Timeout(1), Throws.InstanceOf<OperationCanceledException>());
        wait.First = wait.Second = Task.FromResult(0);
    }

    [TestCase(Type.Uds)]
    public async Task ConsumingACallThatWaitedToBeSentTwice_Throws(Type type)
    {
        await using var wait = await StartCreditWait(type);

        var call = wait.Client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None);
        wait.Release.SetResult();
        Assert.That(await call, Is.EqualTo(1));

        Assert.Throws<InvalidOperationException>(() => call.GetAwaiter().GetResult());
        Assert.That(await wait.Client.Proxy.ServerTaskValueWithCancellation(CancellationToken.None).AsTask().Timeout(1),
            Is.EqualTo(1));
    }

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            base.Post(d, state);
        }
    }
}
