using System.Buffers;
using MemoryPack;
using NexNet.IntegrationTests.TestInterfaces;
using NexNet.Messages;
using NUnit.Framework;
#pragma warning disable VSTHRD200

namespace NexNet.IntegrationTests;

internal partial class NexusClientTests_NexusPipe : BaseTests
{
    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    public async Task ClientReceivesPipeData(Type type)
    {
        var tcs = new TaskCompletionSource();
        var data = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var (server, sNexus, client, cNexus) = CreateServerClient(
            CreateServerConfig(type),
            CreateClientConfig(type));

        server.Start();

        cNexus.ClientTaskValueWithPipeEvent = async (nexus, pipe) =>
        {
            var result = await pipe.Reader.ReadAsync();
            Assert.AreEqual(data, result.Buffer.ToArray());
            tcs.SetResult();
        };

        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await sNexus.ConnectedTCS.Task.WaitAsync(TimeSpan.FromSeconds(1));

        sNexus.OnConnectedEvent = async nexus =>
        {
            var pipe = NexusPipe.Create(async (writer, token) =>
            {
                await writer.WriteAsync(data, token);
                await Task.Delay(10000);
            });
            await nexus.Context.Clients.Caller.ClientTaskValueWithPipe(pipe);
        };
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(100));
    }

    

}
