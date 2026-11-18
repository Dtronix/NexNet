using NexNet.Pipes;
using System.Threading.Tasks;

namespace NexNet.IntegrationTests.Pipes;
public class PipeIdTests
{
    [Test]
    [Arguments((byte)0, (byte)0)]
    [Arguments((byte)1, (byte)0)]
    [Arguments((byte)0, (byte)1)]
    [Arguments((byte)7, (byte)200)]
    [Arguments((byte)255, (byte)255)]
    public async Task ComposeAndExtractRoundTrip(byte clientId, byte serverId)
    {
        var id = NexusPipeManager.ComposeId(clientId, serverId);
        var (c, s) = NexusPipeManager.ExtractClientAndServerId(id);
        await Assert.That(c).IsEqualTo(clientId);
        await Assert.That(s).IsEqualTo(serverId);
    }

    [Test]
    public async Task ComposedValueIsClientLowServerHigh()
    {
        // Defined by the wire spec as (clientId | serverId << 8), independent of host byte order.
        await Assert.That(NexusPipeManager.ComposeId(0x12, 0x34)).IsEqualTo((ushort)0x3412);
    }
}