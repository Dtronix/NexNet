using NUnit.Framework;

namespace NexNet.IntegrationTests;

internal partial class NexNetClientTests : BaseTests
{


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithParam(Type type)
    {
        var tcs = await ClientSendsMessage<NexNet.Messages.InvocationCancellationRequestMessage>(
            type,
            sHub => sHub.ServerTaskWithCancellationEvent = async (hub, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            client => client.Proxy.ServerTaskWithCancellation(new CancellationTokenSource(100).Token)).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskWithValueAndCancellation(Type type)
    {
        var tcs = await ClientSendsMessage<NexNet.Messages.InvocationCancellationRequestMessage>(
            type,
            sHub => sHub.ServerTaskWithValueAndCancellationEvent = async (hub, value, token) =>
            {
                await Task.Delay(10000, token);
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            client => client.Proxy.ServerTaskWithValueAndCancellation(1234, new CancellationTokenSource(100).Token)).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithCancellation(Type type)
    {
        var tcs = await ClientSendsMessage<NexNet.Messages.InvocationCancellationRequestMessage>(
            type,
            sHub => sHub.ServerTaskValueWithCancellationEvent = async (hub, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            async client =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async() => await client.Proxy.ServerTaskValueWithCancellation(new CancellationTokenSource(100).Token));
            }).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientSendsCancellationTokenOnClientSideTimeout_ServerTaskValueWithValueAndCancellation(Type type)
    {
        var tcs = await ClientSendsMessage<NexNet.Messages.InvocationCancellationRequestMessage>(
            type,
            sHub => sHub.ServerTaskValueWithValueAndCancellationEvent = async (hub, value, token) =>
            {
                await Task.Delay(10000, token);
                return 12345;
            },
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            async client =>
            {
                Assert.ThrowsAsync<TaskCanceledException>(async () => await client.Proxy.ServerTaskValueWithValueAndCancellation(1234, new CancellationTokenSource(100).Token));
            }).WaitAsync(TimeSpan.FromSeconds(1));

        await tcs.WaitAsync(TimeSpan.FromSeconds(1));
    }


    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientDoesNotSendCancellationAfterCompletion(Type type)
    {
        var tcs = await ClientSendsMessage<NexNet.Messages.InvocationCancellationRequestMessage>(
            type,
            sHub => sHub.ServerTaskWithCancellationEvent = (hub, token) => ValueTask.CompletedTask,
            (message, source) =>
            {
                Assert.AreEqual(1, message.InvocationId);
                source.TrySetResult();
            },
            client => client.Proxy.ServerTaskWithCancellation(new CancellationTokenSource(100).Token));

        Assert.ThrowsAsync<TimeoutException>(() => tcs.WaitAsync(TimeSpan.FromMilliseconds(200)));
    }


}
