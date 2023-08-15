using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using System.Diagnostics;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable CS1998
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests_Cancellation : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task SendsCancellationTokenOnTimeout_ClientTaskValueWithParam(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            nexus => nexus.ClientTaskWithCancellationEvent = async (nexus, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            nexus =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await nexus.Context.Clients.Caller.ClientTaskWithCancellation(new CancellationTokenSource(100).Token);
                });
                return ValueTask.CompletedTask;
            }).Timeout(1);

        await tcs.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task SendsCancellationTokenOnTimeout_ServerTaskWithValueAndCancellation(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            nexus => nexus.ClientTaskWithValueAndCancellationEvent = async (nexus, value, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            nexus =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(1234, new CancellationTokenSource(100).Token);
                });

                return default;
            }).Timeout(1);

        await tcs.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task SendsCancellationTokenOnTimeout_ServerTaskValueWithCancellation(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            nexus => nexus.ClientTaskValueWithCancellationEvent = async (nexus, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            async nexus =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async() => await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(new CancellationTokenSource(100).Token));
            }).Timeout(1);

        await tcs.Timeout(1);
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task SendsCancellationTokenOnTimeout_ServerTaskValueWithValueAndCancellation(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            nexus => nexus.ClientTaskValueWithValueAndCancellationEvent = async (nexus, value, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            async nexus =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () => await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));
            }).Timeout(1);

        await tcs.Timeout(1);
    }

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task ClientDoesNotSendCancellationAfterCompletion(Type type)
    {
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            nexus => nexus.ClientTaskWithCancellationEvent = (nexus, token) => ValueTask.CompletedTask,
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            nexus => nexus.Context.Clients.Caller.ClientTaskWithCancellation(new CancellationTokenSource(200).Token));

        await AssertThrows<TimeoutException>(() => tcs.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }



    private async Task<Task> ServerSendsMessage<T>(Type type, Action<ClientNexus> setup, Action<T, TaskCompletionSource> onMessage, Func<ServerNexus, ValueTask> action)
        where T : IMessageBase
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var tcs = new TaskCompletionSource();
        var (server, serverNexus, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        setup.Invoke(clientNexus);

        await server.StartAsync();

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


        serverNexus.OnConnectedEvent = nexus =>
        {
            _ = action.Invoke(serverNexus);
            return ValueTask.CompletedTask;
        };


        await client.ConnectAsync().Timeout(1);

        return tcs.Task;
    }


}
