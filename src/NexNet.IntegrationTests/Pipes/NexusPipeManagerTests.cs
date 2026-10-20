using System.Buffers;
using NexNet.IntegrationTests.SessionManagement;
using NexNet.Messages;
using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

[TestFixture]
internal class NexusPipeManagerTests
{
    private static NexNet.Transports.TcpServerConfig CreateConfig() =>
        new() { EndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0) };

    [Test]
    public void BufferIncomingData_UnknownPipe_IsProtocolError()
    {
        var manager = new NexusPipeManager();
        manager.Setup(new MockNexusSession());

        // Pipes stay known while closing, so data the peer sent before learning of a close is still accepted. An ID
        // that is neither open nor closing is a protocol violation.
        var result = manager.BufferIncomingData(NexusPipeManager.ComposeId(1, 1), new ReadOnlySequence<byte>(new byte[10]));

        Assert.That(result, Is.EqualTo(DisconnectReason.ProtocolError));
    }

    [Test]
    public void AddCredit_UnknownPipe_IsIgnored()
    {
        var manager = new NexusPipeManager();
        manager.Setup(new MockNexusSession());

        // A grant can race with this side freeing the pipe.
        Assert.That(manager.AddCredit(NexusPipeManager.ComposeId(1, 1), 1024), Is.EqualTo(DisconnectReason.None));
    }

    [Test]
    public void RentPipe_BeyondMaxPipes_ThrowsBusy()
    {
        var session = new MockNexusSession { Config = CreateConfig() };
        session.Config.MaxPipes = 2;
        var manager = new NexusPipeManager();
        manager.Setup(session);

        manager.RentPipe();
        manager.RentPipe();

        Assert.Throws<NexusBusyException>(() => manager.RentPipe());
    }

    [Test]
    public void RentPipe_BeyondBudget_ThrowsBusyAndReservesNothing()
    {
        var session = new MockNexusSession { Config = CreateConfig() };
        session.Budget = new NexNet.Internals.SessionBudget(session.Config.PipeWindow);
        var manager = new NexusPipeManager();
        manager.Setup(session);

        manager.RentPipe();
        Assert.That(session.Budget.Reserved, Is.EqualTo(session.Config.PipeWindow));

        Assert.Throws<NexusBusyException>(() => manager.RentPipe());
        Assert.That(session.Budget.Reserved, Is.EqualTo(session.Config.PipeWindow));
    }
}
