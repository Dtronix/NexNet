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
