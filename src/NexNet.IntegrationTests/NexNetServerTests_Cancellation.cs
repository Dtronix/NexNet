using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using System.Diagnostics;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable CS1998
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexNetServerTests_Cancellation : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task SendsCancellationTokenOnTimeout_ClientTaskValueWithParam(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationRequestMessage>(
            type,
            hub => hub.ClientTaskWithCancellationEvent = async (hub, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            hub =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await hub.Context.Clients.Caller.ClientTaskWithCancellation(new CancellationTokenSource(100).Token);
                });
                return ValueTask.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task SendsCancellationTokenOnTimeout_ServerTaskWithValueAndCancellation(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationRequestMessage>(
            type,
            hub => hub.ClientTaskWithValueAndCancellationEvent = async (hub, value, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            hub =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await hub.Context.Clients.Caller.ClientTaskWithValueAndCancellation(1234, new CancellationTokenSource(100).Token);
                });

                return default;
            }).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task SendsCancellationTokenOnTimeout_ServerTaskValueWithCancellation(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationRequestMessage>(
            type,
            hub => hub.ClientTaskValueWithCancellationEvent = async (hub, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            async hub =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async() => await hub.Context.Clients.Caller.ClientTaskValueWithCancellation(new CancellationTokenSource(100).Token));
            }).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task SendsCancellationTokenOnTimeout_ServerTaskValueWithValueAndCancellation(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationRequestMessage>(
            type,
            hub => hub.ClientTaskValueWithValueAndCancellationEvent = async (hub, value, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            async hub =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () => await hub.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));
            }).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientDoesNotSendCancellationAfterCompletion(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationRequestMessage>(
            type,
            hub => hub.ClientTaskWithCancellationEvent = (hub, token) => ValueTask.CompletedTask,
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            hub => hub.Context.Clients.Caller.ClientTaskWithCancellation(new CancellationTokenSource(200).Token));

        Assert.ThrowsAsync<TimeoutException>(() => tcs.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }



    private async Task<Task> ServerSendsMessage<T>(Type type, Action<ClientHub> setup, Action<T, TaskCompletionSource> onMessage, Func<ServerHub, ValueTask> action)
        where T : IMessageBodyBase
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type, false);
        var tcs = new TaskCompletionSource();
        var (server, serverHub, client, clientHub) = CreateServerClient(serverConfig, clientConfig);

        setup.Invoke(clientHub);

        server.Start();

        serverConfig.InternalOnSend = (_, bytes) =>
        {
            try
            {
                if (bytes[0] != (byte)T.Type)
                    return;

                var message = MemoryPackSerializer.Deserialize<T>(new ReadOnlySpan<byte>(bytes).Slice(3));
                Debug.Assert(message != null, nameof(message) + " != null");
                onMessage(message, tcs);
            }
            catch
            {
                // not a type we care about
            }
        };


        serverHub.OnConnectedEvent = hub =>
        {
            action?.Invoke(serverHub);
            return ValueTask.CompletedTask;
        };


        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));

        return tcs.Task;
    }


}
