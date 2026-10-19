using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_InvalidInvocations : BaseTests
{
    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ClientThrowsWhenArgumentTooLarge(Type type)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Serialized size always exceeds the limit: the payload alone is MaxArgumentSize bytes plus framing overhead.
        var data = new byte[NexNet.Messages.IInvocationMessage.MaxArgumentSize];
        await AssertThrows<NexusMessageTooLargeException>(() => clientNexus.Context.Proxy.ServerData(data).AsTask())
            .Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task OversizedResultFailsCallAndNextCallSucceeds(Type type)
    {
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // The result does not fit in one frame. It used to leave the session's send buffer dirty, failing every
        // later send while pings kept the session looking alive.
        await AssertThrows<NexNet.Invocation.ProxyRemoteInvocationException>(() => clientNexus.Context.Proxy.ServerDataResult(70_000).AsTask())
            .Timeout(1);

        var result = await clientNexus.Context.Proxy.ServerDataResult(10).Timeout(1);
        Assert.That(result.Length, Is.EqualTo(10));
    }
}
