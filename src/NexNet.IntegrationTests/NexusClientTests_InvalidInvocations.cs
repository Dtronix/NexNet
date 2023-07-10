using MemoryPack;
using Newtonsoft.Json.Linq;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_InvalidInvocations : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientThrowsWhenArgumentTooLarge(Type type)
    {
        var tcs = new TaskCompletionSource();
        var (server, serverNexus, client, clientNexus) = CreateServerClient(
            CreateServerConfig(type, false),
            CreateClientConfig(type, false));

        server.Start();
        await client.ConnectAsync();
        await client.ReadyTask.WaitAsync(TimeSpan.FromSeconds(1));

        var data = new byte[65521];
        Assert.Throws<ArgumentOutOfRangeException>(() => clientNexus.Context.Proxy.ServerData(data));
    }


}
