using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_InvalidInvocations : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task ClientThrowsWhenArgumentTooLarge(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        await server.StartAsync();
        await client.ConnectAsync().Timeout(1);

        var data = new byte[65521];
        await AssertThrows<ArgumentOutOfRangeException>(() => clientNexus.Context.Proxy.ServerData(data).AsTask())
            .Timeout(1);
    }


}
