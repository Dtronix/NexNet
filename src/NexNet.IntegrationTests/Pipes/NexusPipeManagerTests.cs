using System.Buffers;
using NexNet.IntegrationTests.SessionManagement;
using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

[TestFixture]
internal class NexusPipeManagerTests
{
    [Test]
    public async Task BufferIncomingData_UnknownPipe_IsDiscarded()
    {
        var manager = new NexusPipeManager();
        manager.Setup(new MockNexusSession());

        // Data can arrive after the local side closed the pipe. It used to throw, which disconnected the session.
        var result = await manager.BufferIncomingData(NexusPipeManager.ComposeId(1, 1), new ReadOnlySequence<byte>(new byte[10]));

        Assert.That(result, Is.EqualTo(NexusPipeBufferResult.DataIgnored));
    }
}
