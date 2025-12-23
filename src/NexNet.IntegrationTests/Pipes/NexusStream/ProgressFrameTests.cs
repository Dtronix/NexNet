using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class ProgressFrameTests
{
    [Test]
    public void Size_Is33()
    {
        // 8 (bytesTransferred) + 8 (totalBytes) + 8 (elapsedTicks) + 8 (bytesPerSecond) + 1 (state) = 33
        Assert.That(ProgressFrame.Size, Is.EqualTo(33));
    }

    [Test]
    public void Constructor_SetsAllFields()
    {
        var frame = new ProgressFrame(1000, 5000, 123456789L, 500.5, TransferState.Active);

        Assert.That(frame.BytesTransferred, Is.EqualTo(1000));
        Assert.That(frame.TotalBytes, Is.EqualTo(5000));
        Assert.That(frame.ElapsedTicks, Is.EqualTo(123456789L));
        Assert.That(frame.BytesPerSecond, Is.EqualTo(500.5));
        Assert.That(frame.State, Is.EqualTo(TransferState.Active));
    }

    [Test]
    public void GetPayloadSize_Returns33()
    {
        var frame = new ProgressFrame();
        Assert.That(frame.GetPayloadSize(), Is.EqualTo(33));
    }

    [Test]
    public void Roundtrip_Active()
    {
        var original = new ProgressFrame(5000, 10000, 1000000L, 1024.5, TransferState.Active);
        var buffer = new byte[ProgressFrame.Size];
        original.Write(buffer);

        var parsed = ProgressFrame.Read(buffer);

        Assert.That(parsed.BytesTransferred, Is.EqualTo(5000));
        Assert.That(parsed.TotalBytes, Is.EqualTo(10000));
        Assert.That(parsed.ElapsedTicks, Is.EqualTo(1000000L));
        Assert.That(parsed.BytesPerSecond, Is.EqualTo(1024.5));
        Assert.That(parsed.State, Is.EqualTo(TransferState.Active));
    }

    [Test]
    public void Roundtrip_Complete()
    {
        var original = new ProgressFrame(10000, 10000, 5000000L, 2000.0, TransferState.Complete);
        var buffer = new byte[ProgressFrame.Size];
        original.Write(buffer);

        var parsed = ProgressFrame.Read(buffer);

        Assert.That(parsed.BytesTransferred, Is.EqualTo(10000));
        Assert.That(parsed.TotalBytes, Is.EqualTo(10000));
        Assert.That(parsed.State, Is.EqualTo(TransferState.Complete));
    }

    [Test]
    public void Roundtrip_Failed()
    {
        var original = new ProgressFrame(3000, 10000, 2000000L, 1500.0, TransferState.Failed);
        var buffer = new byte[ProgressFrame.Size];
        original.Write(buffer);

        var parsed = ProgressFrame.Read(buffer);

        Assert.That(parsed.BytesTransferred, Is.EqualTo(3000));
        Assert.That(parsed.State, Is.EqualTo(TransferState.Failed));
    }

    [Test]
    public void Roundtrip_Paused()
    {
        var original = new ProgressFrame(7500, 15000, 3000000L, 0.0, TransferState.Paused);
        var buffer = new byte[ProgressFrame.Size];
        original.Write(buffer);

        var parsed = ProgressFrame.Read(buffer);

        Assert.That(parsed.BytesTransferred, Is.EqualTo(7500));
        Assert.That(parsed.BytesPerSecond, Is.EqualTo(0.0));
        Assert.That(parsed.State, Is.EqualTo(TransferState.Paused));
    }

    [Test]
    public void Roundtrip_UnknownTotal()
    {
        var original = new ProgressFrame(5000, -1, 1000000L, 500.0, TransferState.Active);
        var buffer = new byte[ProgressFrame.Size];
        original.Write(buffer);

        var parsed = ProgressFrame.Read(buffer);

        Assert.That(parsed.TotalBytes, Is.EqualTo(-1));
    }

    [Test]
    public void Roundtrip_LargeValues()
    {
        var original = new ProgressFrame(
            long.MaxValue / 2,
            long.MaxValue,
            long.MaxValue / 4,
            double.MaxValue / 2,
            TransferState.Active);
        var buffer = new byte[ProgressFrame.Size];
        original.Write(buffer);

        var parsed = ProgressFrame.Read(buffer);

        Assert.That(parsed.BytesTransferred, Is.EqualTo(long.MaxValue / 2));
        Assert.That(parsed.TotalBytes, Is.EqualTo(long.MaxValue));
        Assert.That(parsed.ElapsedTicks, Is.EqualTo(long.MaxValue / 4));
        Assert.That(parsed.BytesPerSecond, Is.EqualTo(double.MaxValue / 2));
    }

    [Test]
    public void ToString_ContainsKeyInfo()
    {
        var frame = new ProgressFrame(5000, 10000, 1000000L, 500.0, TransferState.Active);
        var str = frame.ToString();

        Assert.That(str, Does.Contain("5000"));
        Assert.That(str, Does.Contain("10000"));
        Assert.That(str, Does.Contain("Active"));
    }
}
