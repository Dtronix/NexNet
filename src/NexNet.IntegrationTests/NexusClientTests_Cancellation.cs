﻿using MemoryPack;
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
            client =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await client.Proxy.ServerTaskWithCancellation(new CancellationTokenSource(100).Token);
                });
                return ValueTask.CompletedTask;
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
            client =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    await client.Proxy.ServerTaskWithValueAndCancellation(1234, new CancellationTokenSource(100).Token);
                });

                return default;
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
                Assert.ThrowsAsync<TaskCanceledException>(async() => await client.Proxy.ServerTaskValueWithCancellation(new CancellationTokenSource(100).Token));
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
                Assert.ThrowsAsync<TaskCanceledException>(async () => await client.Proxy.ServerTaskValueWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));
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



    private async Task<Task> ClientSendsMessage<T>(Type type, Action<ServerNexus> setup, Action<T, TaskCompletionSource> onMessage, Func<NexusClient<ClientNexus, ClientNexus.ServerProxy>, ValueTask> action)
        where T : IMessageBase
    {
        var clientConfig = CreateClientConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, serverNexus, client, _) = CreateServerClient(
            CreateServerConfig(type),
            clientConfig);

        setup.Invoke(serverNexus);

        await server.StartAsync().Timeout(1);

        clientConfig.InternalOnSend = (_, bytes) =>
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

        await client.ConnectAsync().Timeout(1);

        await action.Invoke(client);

        return tcs.Task;
    }


}
