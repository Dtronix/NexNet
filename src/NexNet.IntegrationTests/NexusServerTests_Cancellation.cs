using NexNet.IntegrationTests.TestInterfaces;
using System.Diagnostics;
using NexNet.Messages;
#pragma warning disable CS1998
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusServerTests_Cancellation : BaseTests
{
    [Test]
    [Transports]
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
                CallbackAssert.Equal(message.InvocationId, 1);
                source.TrySetResult();
            },
            async nexus =>
            {
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                {
                    await nexus.Context.Clients.Caller.ClientTaskWithCancellation(new CancellationTokenSource(100).Token);
                });
            }).Timeout(T(1));

        await tcs.Timeout(T(1));
    }


    [Test]
    [Transports]
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
                CallbackAssert.Equal(message.InvocationId, 1);
                source.TrySetResult();
            },
            async nexus =>
            {
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
                {
                    await nexus.Context.Clients.Caller.ClientTaskWithValueAndCancellation(1234, new CancellationTokenSource(100).Token);
                });
            }).Timeout(T(1));

        await tcs.Timeout(T(1));
    }


    [Test]
    [Transports]
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
                CallbackAssert.Equal(message.InvocationId, 1);
                source.TrySetResult();
            },
            async nexus =>
            {
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(async() => await nexus.Context.Clients.Caller.ClientTaskValueWithCancellation(new CancellationTokenSource(100).Token));
            }).Timeout(T(1));

        await tcs.Timeout(T(1));
    }


    [Test]
    [Transports]
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
                CallbackAssert.Equal(message.InvocationId, 1);
                source.TrySetResult();
            },
            async nexus =>
            {
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await nexus.Context.Clients.Caller.ClientTaskValueWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));
            }).Timeout(T(1));

        await tcs.Timeout(T(1));
    }

    [Test]
    [Transports]
    public async Task ClientDoesNotSendCancellationAfterCompletion(Type type)
    {
        using var cts = new CancellationTokenSource();
        var completed = new TaskCompletionSource<ServerNexus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = await ServerSendsMessage<Messages.InvocationCancellationMessage>(
            type,
            nexus => nexus.ClientTaskWithCancellationEvent = (nexus, token) => ValueTask.CompletedTask,
            (message, source) =>
            {
                CallbackAssert.Equal(message.InvocationId, 1);
                source.TrySetResult();
            },
            async nexus =>
            {
                await nexus.Context.Clients.Caller.ClientTaskWithCancellation(cts.Token);
                completed.TrySetResult(nexus);
            });

        var serverNexus = await completed.Task.Timeout(T(1));

        // Canceled after the call completed: there is nothing left to cancel.
        cts.Cancel();
        await Barrier(serverNexus.Context.Clients.Caller);
        await Assert.That(tcs.IsCompleted).IsFalse().Because("The server must not send a cancellation after the call completed.");
    }



    private async Task<Task> ServerSendsMessage<TMessage>(Type type, Action<ClientNexus> setup, Action<TMessage, TaskCompletionSource> onMessage, Func<ServerNexus, ValueTask> action)
        where TMessage : class, IMessageBase, new()
    {
        var clientConfig = CreateClientConfig(type);
        var serverConfig = CreateServerConfig(type);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(serverConfig, clientConfig);

        setup.Invoke(clientNexus);

        await server.StartAsync().Timeout(T(TestTimeouts.Connect));

        ServerTaps.OnSend(serverConfig, (_, frame) =>
        {
            if (frame.Type != TMessage.Type)
                return;

            var message = TestSerialization.DeserializeMessage<TMessage>(frame.Body);
            Debug.Assert(message != null, nameof(message) + " != null");
            onMessage(message, tcs);
        });


        server.OnNexusCreated = nexus => nexus.OnConnectedEvent = nexus2 =>
        {
            _ = action.Invoke(nexus2);
            return ValueTask.CompletedTask;
        };


        await client.ConnectAsync().Timeout(T(TestTimeouts.Connect));

        return tcs.Task;
    }


}