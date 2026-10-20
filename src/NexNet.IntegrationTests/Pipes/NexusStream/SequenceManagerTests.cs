using System;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class SequenceManagerTests
{
    [Test]
    public void InitialState_ZeroSequences()
    {
        var manager = new SequenceManager();

        Assert.That(manager.NextExpected, Is.EqualTo(0));
        Assert.That(manager.NextToSend, Is.EqualTo(0));
    }

    [Test]
    public void GetNextSendSequence_IncrementsCounter()
    {
        var manager = new SequenceManager();

        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(0));
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(1));
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(2));
        Assert.That(manager.NextToSend, Is.EqualTo(3));
    }

    [Test]
    public void PeekNextSendSequence_DoesNotIncrement()
    {
        var manager = new SequenceManager();

        Assert.That(manager.PeekNextSendSequence(), Is.EqualTo(0));
        Assert.That(manager.PeekNextSendSequence(), Is.EqualTo(0));
        Assert.That(manager.NextToSend, Is.EqualTo(0));
    }

    [Test]
    public void ValidateReceived_ValidSequence_ReturnsTrue()
    {
        var manager = new SequenceManager();

        Assert.That(manager.ValidateReceived(0), Is.True);
        Assert.That(manager.ValidateReceived(1), Is.True);
        Assert.That(manager.ValidateReceived(2), Is.True);
    }

    [Test]
    public void ValidateReceived_InvalidSequence_ReturnsFalse()
    {
        var manager = new SequenceManager();

        // Skip sequence 0, try to validate 1
        Assert.That(manager.ValidateReceived(1), Is.False);
    }

    [Test]
    public void ValidateReceived_DuplicateSequence_ReturnsFalse()
    {
        var manager = new SequenceManager();

        Assert.That(manager.ValidateReceived(0), Is.True);
        // Try to validate 0 again
        Assert.That(manager.ValidateReceived(0), Is.False);
    }

    [Test]
    public void ValidateReceived_UpdatesNextExpected()
    {
        var manager = new SequenceManager();

        manager.ValidateReceived(0);
        Assert.That(manager.NextExpected, Is.EqualTo(1));

        manager.ValidateReceived(1);
        Assert.That(manager.NextExpected, Is.EqualTo(2));
    }

    [Test]
    public void ValidateReceivedOrThrow_ValidSequence_DoesNotThrow()
    {
        var manager = new SequenceManager();

        Assert.DoesNotThrow(() => manager.ValidateReceivedOrThrow(0));
        Assert.DoesNotThrow(() => manager.ValidateReceivedOrThrow(1));
    }

    [Test]
    public void ValidateReceivedOrThrow_InvalidSequence_Throws()
    {
        var manager = new SequenceManager();

        var ex = Assert.Throws<InvalidOperationException>(() => manager.ValidateReceivedOrThrow(1));
        Assert.That(ex.Message, Does.Contain("expected"));
        Assert.That(ex.Message, Does.Contain("0")); // Expected 0
        Assert.That(ex.Message, Does.Contain("1")); // Got 1
    }

    [Test]
    public void Reset_ClearsBothCounters()
    {
        var manager = new SequenceManager();

        // Advance both counters
        manager.GetNextSendSequence();
        manager.GetNextSendSequence();
        manager.ValidateReceived(0);
        manager.ValidateReceived(1);

        manager.Reset();

        Assert.That(manager.NextToSend, Is.EqualTo(0));
        Assert.That(manager.NextExpected, Is.EqualTo(0));
    }

    [Test]
    public void ContinuousSequences_AcrossMultipleOperations()
    {
        var manager = new SequenceManager();

        // Simulate multiple write operations
        // Each should get the next sequence number (continuous)

        // First write
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(0));
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(1));
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(2));

        // Second write (continues from where we left off)
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(3));
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(4));

        // Third write
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(5));
    }

    [Test]
    public void ContinuousSequences_ReceiveAcrossMultipleOperations()
    {
        var manager = new SequenceManager();

        // Simulate multiple read operations
        // Sequence must be continuous across operations

        // First read
        Assert.That(manager.ValidateReceived(0), Is.True);
        Assert.That(manager.ValidateReceived(1), Is.True);

        // Second read (continues from where we left off)
        Assert.That(manager.ValidateReceived(2), Is.True);
        Assert.That(manager.ValidateReceived(3), Is.True);

        // Cannot go back
        Assert.That(manager.ValidateReceived(0), Is.False);
        Assert.That(manager.ValidateReceived(2), Is.False);
    }

    [Test]
    public void UInt32Overflow_Wraps()
    {
        var manager = new SequenceManager();

        // Use reflection to set internal state near max value
        var field = typeof(SequenceManager).GetField("_nextToSend",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(manager, uint.MaxValue);

        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(uint.MaxValue));
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(0)); // Wraps around
    }

    [Test]
    public void SendAndReceive_Independent()
    {
        var manager = new SequenceManager();

        // Send and receive sequences are independent
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(0));
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(1));

        // Receiving still starts at 0
        Assert.That(manager.ValidateReceived(0), Is.True);
        Assert.That(manager.ValidateReceived(1), Is.True);

        // Sending continues
        Assert.That(manager.GetNextSendSequence(), Is.EqualTo(2));
    }
}
