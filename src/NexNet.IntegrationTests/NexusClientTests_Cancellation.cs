using NexNet.IntegrationTests.TestInterfaces;
using System.Diagnostics;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable CS1998
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_Cancellation : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithParam(Type type)
    {
        var tcs = await ClientSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            sNexus => sNexus.ServerTaskWithCancellationEvent = async (nexus, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.That(message.InvocationId, Is.EqualTo(1));
                source.TrySetResult();
            },
            async client =>
            {
                await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await client.Proxy.ServerTaskWithCancellation(new CancellationTokenSource(100).Token);
                });
            }).Timeout(1);

        await tcs.Timeout(1);
    }


    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskWithValueAndCancellation(Type type)
    {
        var tcs = await ClientSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            sNexus => sNexus.ServerTaskWithValueAndCancellationEvent = async (nexus, value, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.That(message.InvocationId, Is.EqualTo(1));
                source.TrySetResult();
            },
            async client =>
            {
                await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await client.Proxy.ServerTaskWithValueAndCancellation(1234, new CancellationTokenSource(100).Token);
                });
            }).Timeout(1);

        await tcs.Timeout(1);
    }


    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithCancellation(Type type)
    {
        var tcs = await ClientSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            sNexus => sNexus.ServerTaskValueWithCancellationEvent = async (nexus, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.That(message.InvocationId, Is.EqualTo(1));
                source.TrySetResult();
            },
            async client =>
            {
                await Assert.ThrowsAsync<TaskCanceledException>(async() => await client.Proxy.ServerTaskValueWithCancellation(new CancellationTokenSource(100).Token));
            }).Timeout(1);

        await tcs.Timeout(1);
    }


    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithValueAndCancellation(Type type)
    {
        var tcs = await ClientSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            sNexus => sNexus.ServerTaskValueWithValueAndCancellationEvent = async (nexus, value, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.That(message.InvocationId, Is.EqualTo(1));
                source.TrySetResult();
            },
            async client =>
            {
                await Assert.ThrowsAsync<TaskCanceledException>(async () => await client.Proxy.ServerTaskValueWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));
            }).Timeout(1);

        await tcs.Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientDoesNotSendCancellationAfterCompletion(Type type)
    {
        var tcs = await ClientSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            sNexus => sNexus.ServerTaskWithCancellationEvent = (nexus, token) => ValueTask.CompletedTask,
            (message, source) =>
            {
                Assert.That(message.InvocationId, Is.EqualTo(1));
                source.TrySetResult();
            },
            client => client.Proxy.ServerTaskWithCancellation(new CancellationTokenSource(200).Token));

        await AssertThrows<TimeoutException>(() => tcs.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.WebSocket)]
    public async Task LateResultOfCanceledInvocationDoesNotCompleteLaterInvocation(Type type)
    {
        // The server method ignores the token, so the canceled call still sends its result, after the next call starts.
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));

        server.OnNexusCreated = nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, value, _) =>
        {
            if (value == 1)
            {
                await firstGate.Task;
                firstSent.TrySetResult();
            }
            else
            {
                await secondGate.Task;
            }

            return value * 10;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var cts = new CancellationTokenSource();
        var first = client.Proxy.ServerTaskValueWithValueAndCancellation(1, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await first.Timeout(1));

        var second = client.Proxy.ServerTaskValueWithValueAndCancellation(2, CancellationToken.None).AsTask();
        firstGate.SetResult();
        await firstSent.Task.Timeout(1);

        // Give the late result time to arrive before the second call's own result.
        await Task.Delay(100);
        secondGate.SetResult();

        Assert.That(await second.Timeout(1), Is.EqualTo(20));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task CanceledInvocationReleasesItsIdOnTheCanceledReply(Type type)
    {
        // The server honors the token and replies Canceled; the id is reserved until then.
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, value, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return value;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        using var cts = new CancellationTokenSource();
        var call = client.Proxy.ServerTaskValueWithValueAndCancellation(1, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await call.Timeout(1));

        var stateManager = client.Session!.SessionInvocationStateManager;
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (stateManager.IsOutstanding(1) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.That(stateManager.IsOutstanding(1), Is.False);
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task CancellationOfAQueuedInvocation_CancelsItWhenItStarts(Type type)
    {
        // One execution slot: the second call is still queued on the server when its cancellation arrives.
        var serverConfig = CreateServerConfig(type);
        serverConfig.MaxConcurrentConnectionInvocations = 1;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(serverConfig, CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueWithValueAndCancellationEvent = async (_, value, token) =>
        {
            if (value == 1)
            {
                started.TrySetResult();
                await release.Task;
                return value;
            }

            await Task.Delay(Timeout.Infinite, token);
            return value;
        };

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        var first = client.Proxy.ServerTaskValueWithValueAndCancellation(1, CancellationToken.None).AsTask();
        await started.Task.Timeout(1);

        using var cts = new CancellationTokenSource();
        var second = client.Proxy.ServerTaskValueWithValueAndCancellation(2, cts.Token).AsTask();
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await second.Timeout(1));
        await Task.Delay(100);

        release.SetResult();
        Assert.That(await first.Timeout(1), Is.EqualTo(1));

        // The second call starts with its token already canceled and replies Canceled, releasing its id.
        var stateManager = client.Session!.SessionInvocationStateManager;
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (stateManager.IsOutstanding(2) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.That(stateManager.IsOutstanding(2), Is.False);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    public async Task RemoteMethodCanceledOnItsOwn_CancelsTheCaller(Type type)
    {
        // Without a reply the caller, which has no token, would wait forever.
        var (server, client, _) = CreateServerClient(CreateServerConfig(type), CreateClientConfig(type));
        server.OnNexusCreated = nexus => nexus.ServerTaskValueEvent = _ => throw new OperationCanceledException();

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await client.Proxy.ServerTaskValue().AsTask().Timeout(1));
        Assert.That(client.State, Is.EqualTo(ConnectionState.Connected));
    }



    private async Task<Task> ClientSendsMessage<T>(
        Type type, Action<ServerNexus> setup,
        Action<T, TaskCompletionSource> onMessage,
        Func<NexusClient<ClientNexus, ClientNexus.ServerProxy>, ValueTask> action)
        where T : class, IMessageBase, new()
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        server.OnNexusCreated = setup.Invoke;

        await server.StartAsync().Timeout(1);

        clientConfig.InternalOnSend = (_, bytes) =>
        {
            try
            {
                if (bytes[0] != (byte)T.Type)
                    return;

                var message = TestSerialization.DeserializeMessage<T>(new ReadOnlySpan<byte>(bytes).Slice(3));
                Debug.Assert(message != null, nameof(message) + " != null");
                onMessage(message, tcs);
            }
            catch
            {
                // not a type we care about
            }
        };

        await client.ConnectAsync().Timeout(1);

        await action.Invoke(client);

        return tcs.Task;
    }


}
