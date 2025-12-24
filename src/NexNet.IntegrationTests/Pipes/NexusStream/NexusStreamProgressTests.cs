using System;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamProgressTests
{
    [Test]
    public void Init_SetsAllProperties()
    {
        var progress = new NexusStreamProgress
        {
            BytesRead = 1000,
            BytesWritten = 500,
            TotalReadBytes = 5000,
            TotalWriteBytes = 2500,
            Elapsed = TimeSpan.FromSeconds(10),
            ReadBytesPerSecond = 100.0,
            WriteBytesPerSecond = 50.0,
            State = TransferState.Active
        };

        Assert.That(progress.BytesRead, Is.EqualTo(1000));
        Assert.That(progress.BytesWritten, Is.EqualTo(500));
        Assert.That(progress.TotalReadBytes, Is.EqualTo(5000));
        Assert.That(progress.TotalWriteBytes, Is.EqualTo(2500));
        Assert.That(progress.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(progress.ReadBytesPerSecond, Is.EqualTo(100.0));
        Assert.That(progress.WriteBytesPerSecond, Is.EqualTo(50.0));
        Assert.That(progress.State, Is.EqualTo(TransferState.Active));
    }

    [Test]
    public void UnknownTotals_UseMinus1()
    {
        var progress = new NexusStreamProgress
        {
            BytesRead = 100,
            BytesWritten = 50,
            TotalReadBytes = -1,
            TotalWriteBytes = -1,
            State = TransferState.Active
        };

        Assert.That(progress.TotalReadBytes, Is.EqualTo(-1));
        Assert.That(progress.TotalWriteBytes, Is.EqualTo(-1));
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var progress = new NexusStreamProgress
        {
            BytesRead = 1234,
            BytesWritten = 5678,
            TotalReadBytes = 10000,
            TotalWriteBytes = 20000,
            State = TransferState.Active
        };

        var str = progress.ToString();

        Assert.That(str, Does.Contain("1234"));
        Assert.That(str, Does.Contain("5678"));
        Assert.That(str, Does.Contain("Active"));
    }

    [Test]
    public void AllTransferStates_AreValid()
    {
        foreach (TransferState state in Enum.GetValues<TransferState>())
        {
            var progress = new NexusStreamProgress { State = state };
            Assert.That(progress.State, Is.EqualTo(state));
        }
    }
}
