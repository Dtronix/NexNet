using System.Collections.Concurrent;
using NexNet.Internals;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal class InvocationInterceptorTests : BaseTests
{
    [TestCase(Type.Tcp)]
    public async Task NoInterceptor_InvocationsRunDirectly(Type type)
    {
        // Sanity check: when no interceptor is installed, server invocations dispatch normally.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.ServerVoidWithParamEvent = (_, p) =>
        {
            if (p == 999) tcs.SetResult();
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        client.Proxy.ServerVoidWithParam(999);

        await tcs.Task.Timeout(1);
    }

    [TestCase(Type.Tcp)]
    public async Task Interceptor_WrapsEveryInvocation(Type type)
    {
        var interceptor = new CountingInterceptor();
        var serverConfig = CreateServerConfig(type);
        serverConfig.InvocationInterceptor = interceptor;

        var invocationRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.ServerVoidWithParamEvent = (_, p) =>
        {
            if (p == 42) invocationRan.SetResult();
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        client.Proxy.ServerVoidWithParam(42);

        await invocationRan.Task.Timeout(1);
        // Give the interceptor's finally block a moment to record completion.
        await interceptor.WaitForCompletionAsync().Timeout(1);

        Assert.That(interceptor.WrapStarted, Is.GreaterThanOrEqualTo(1));
        Assert.That(interceptor.WrapCompleted, Is.EqualTo(interceptor.WrapStarted));
        Assert.That(interceptor.ObservedMessages, Has.Some.Matches<InvocationMessage>(m => m != null));
    }

    [TestCase(Type.Tcp)]
    public async Task Interceptor_CanShortCircuitDispatch(Type type)
    {
        // If WrapAsync chooses not to invoke the inner delegate, the nexus method must not run.
        // Demonstrates that the hook gives full control over dispatch.
        var interceptor = new SkippingInterceptor();
        var serverConfig = CreateServerConfig(type);
        serverConfig.InvocationInterceptor = interceptor;

        var serverInvoked = false;
        var (server, client, _) = CreateServerClient(
            serverConfig,
            CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.ServerVoidWithParamEvent = (_, _) =>
        {
            serverInvoked = true;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        client.Proxy.ServerVoidWithParam(1);

        await interceptor.SeenInvocation.Task.Timeout(1);
        // The server method must NOT have been called.
        Assert.That(serverInvoked, Is.False);
    }

    private sealed class CountingInterceptor : IInvocationInterceptor
    {
        public int WrapStarted;
        public int WrapCompleted;
        public ConcurrentBag<InvocationMessage> ObservedMessages { get; } = new();
        private TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WrapAsync(InvocationMessage message, Func<ValueTask> invoke)
        {
            Interlocked.Increment(ref WrapStarted);
            ObservedMessages.Add(message);
            try
            {
                await invoke().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Increment(ref WrapCompleted);
                _completion.TrySetResult();
            }
        }

        public Task WaitForCompletionAsync() => _completion.Task;
    }

    private sealed class SkippingInterceptor : IInvocationInterceptor
    {
        public TaskCompletionSource SeenInvocation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WrapAsync(InvocationMessage message, Func<ValueTask> invoke)
        {
            SeenInvocation.TrySetResult();
            // Intentionally do NOT call invoke().
            return ValueTask.CompletedTask;
        }
    }
}
