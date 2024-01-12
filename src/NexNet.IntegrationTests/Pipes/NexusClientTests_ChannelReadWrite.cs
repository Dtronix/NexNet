using System.Buffers;
using System.IO.Pipelines;
using NexNet.Pipes;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusClientTests_ChannelReadWrite : BasePipeTests
{

    [TestCase(Type.Uds)]
    [TestCase(Type.Tcp)]
    [TestCase(Type.TcpTls)]
    [TestCase(Type.Quic)]
    public async Task SendsAndReceivesDifferentTypesOnDuplexPipe(Type type)
    {
        var (_, sNexus, client, cNexus, tcs) = await Setup(type, LogMode.Always);

        int toWrite = 1000000;

        sNexus.ServerTaskValueWithDuplexPipeEvent = async (nexus, pipe) =>
        {
            var reader = await pipe.GetUnmanagedChannelReader<long>().Timeout(1);
            var readLength = 0;
            await reader.ReadBatchUntilComplete(list =>
            {
                readLength += list.Count;
            }).Timeout(1);
            
            Assert.AreEqual(toWrite, readLength);

            var writer = await pipe.GetUnmanagedChannelWriter<int>().Timeout(1);

            await writer.WriteAndComplete(Enumerable.Range(0, toWrite), 100).Timeout(1);
        };

        var pipe = client.CreatePipe();

        await client.Proxy.ServerTaskValueWithDuplexPipe(pipe).Timeout(1);
        
        var writer = await pipe.GetUnmanagedChannelWriter<long>().Timeout(1);
        await writer.WriteAndComplete(Enumerable.Range(0, toWrite).Select(i => (long)i), 100).Timeout(1);
        
        var reader = await pipe.GetUnmanagedChannelReader<int>().Timeout(1);

        var readLength = 0;
        await reader.ReadBatchUntilComplete(list =>
        {
            readLength += list.Count;
        }).Timeout(1);

        Assert.AreEqual(toWrite, readLength);
    }
}
