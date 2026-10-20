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

        // Larger than the server's MaxMessageSize (4 MiB by default): refused before anything is sent.
        var data = new byte[4 * 1024 * 1024 + 1];
        await AssertThrows<NexusMessageTooLargeException>(() => clientNexus.Context.Proxy.ServerData(data).AsTask())
            .Timeout(1);
    }

    [TestCase(Type.Quic)]
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.WebSocket)]
    [TestCase(Type.HttpSocket)]
    public async Task ResultOverMaxMessageSizeFailsCallAndNextCallSucceeds(Type type)
    {
        var (server, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync().Timeout(1);
        await client.ConnectAsync().Timeout(1);

        // Larger than the client accepts (4 MiB by default). It used to leave the session's send buffer dirty,
        // failing every later send while pings kept the session looking alive.
        await AssertThrows<NexNet.Invocation.ProxyRemoteInvocationException>(() => clientNexus.Context.Proxy.ServerDataResult(5 * 1024 * 1024).AsTask())
            .Timeout(5);

        var result = await clientNexus.Context.Proxy.ServerDataResult(10).Timeout(1);
        Assert.That(result.Length, Is.EqualTo(10));
    }
}
