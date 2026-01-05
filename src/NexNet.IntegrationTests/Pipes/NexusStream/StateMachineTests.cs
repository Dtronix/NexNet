using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

/// <summary>
/// Tests for the NexusStreamState enum values.
/// </summary>
[TestFixture]
public class StateMachineTests
{
    [Test]
    public void NexusStreamState_HasExpectedValues()
    {
        Assert.That((int)NexusStreamState.None, Is.EqualTo(0));
        Assert.That((int)NexusStreamState.Opening, Is.EqualTo(1));
        Assert.That((int)NexusStreamState.Open, Is.EqualTo(2));
        Assert.That((int)NexusStreamState.Closed, Is.EqualTo(3));
    }

    [Test]
    public void NexusStreamState_DefaultIsNone()
    {
        NexusStreamState state = default;
        Assert.That(state, Is.EqualTo(NexusStreamState.None));
    }
}
