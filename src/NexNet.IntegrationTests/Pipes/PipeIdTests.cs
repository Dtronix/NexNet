using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

[TestFixture]
public class PipeIdTests
{
    [TestCase((byte)0, (byte)0)]
    [TestCase((byte)1, (byte)0)]
    [TestCase((byte)0, (byte)1)]
    [TestCase((byte)7, (byte)200)]
    [TestCase((byte)255, (byte)255)]
    public void ComposeAndExtractRoundTrip(byte clientId, byte serverId)
    {
        var id = NexusPipeManager.ComposeId(clientId, serverId);
        var (c, s) = NexusPipeManager.ExtractClientAndServerId(id);
        Assert.That(c, Is.EqualTo(clientId));
        Assert.That(s, Is.EqualTo(serverId));
    }

    [Test]
    public void ComposedValueIsClientLowServerHigh()
    {
        // Defined by the wire spec as (clientId | serverId << 8), independent of host byte order.
        Assert.That(NexusPipeManager.ComposeId(0x12, 0x34), Is.EqualTo((ushort)0x3412));
    }
}
