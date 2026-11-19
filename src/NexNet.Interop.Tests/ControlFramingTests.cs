using System.Buffers.Binary;
using System.Text;
using NexNet.Interop.Remote;

namespace NexNet.Interop.Tests;

public class ControlFramingTests
{
    [Test]
    public async Task Frame_RoundTrips_AcrossPartialReads()
    {
        using var buffer = new MemoryStream();
        await ControlFraming.WriteAsync(buffer, ControlFrameKind.Json, Encoding.UTF8.GetBytes("{\"t\":\"evt\"}"));
        await ControlFraming.WriteAsync(buffer, ControlFrameKind.Json, Encoding.UTF8.GetBytes("{}"));

        var bytes = buffer.ToArray();
        await Assert.That(BinaryPrimitives.ReadUInt32BigEndian(bytes)).IsEqualTo(12u); // kind + 11 payload bytes
        await Assert.That(bytes[4]).IsEqualTo((byte)0x01);

        await using var trickle = new TrickleStream(bytes);
        var first = await ControlFraming.ReadAsync(trickle);
        var second = await ControlFraming.ReadAsync(trickle);
        var end = await ControlFraming.ReadAsync(trickle);
        await Assert.That(Encoding.UTF8.GetString(first!.Value.Payload)).IsEqualTo("{\"t\":\"evt\"}");
        await Assert.That(Encoding.UTF8.GetString(second!.Value.Payload)).IsEqualTo("{}");
        await Assert.That(end).IsNull();
    }

    [Test]
    public async Task Write_RefusesFramesAbove64MiB()
    {
        using var buffer = new MemoryStream();
        var payload = new byte[ControlFraming.MaxFrameSize];
        await Assert.ThrowsExactlyAsync<ControlProtocolException>(
            async () => await ControlFraming.WriteAsync(buffer, ControlFrameKind.Json, payload));
        await Assert.That(buffer.Length).IsEqualTo(0L);
    }

    [Test]
    [Arguments(0u, (byte)1, "zero length")]
    [Arguments(64u * 1024 * 1024 + 1, (byte)1, "exceeds")]
    [Arguments(3u, (byte)2, "reserved")]
    [Arguments(3u, (byte)7, "Unknown control frame kind")]
    public async Task Read_RefusesBadHeaders(uint length, byte kind, string message)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, length);
        bytes[4] = kind;
        using var stream = new MemoryStream(bytes);
        var error = await Assert.ThrowsExactlyAsync<ControlProtocolException>(async () => await ControlFraming.ReadAsync(stream));
        await Assert.That(error!.Message).Contains(message);
    }

    [Test]
    public async Task Read_RefusesTheEndOfTheStreamInsideAFrame()
    {
        var bytes = new byte[] { 0, 0, 0, 10, 1, (byte)'{' };
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsExactlyAsync<ControlProtocolException>(async () => await ControlFraming.ReadAsync(stream));

        using var header = new MemoryStream([0, 0]);
        await Assert.ThrowsExactlyAsync<ControlProtocolException>(async () => await ControlFraming.ReadAsync(header));
    }

    [Test]
    public async Task Connection_FailsOnAnOversizedFrameFromTheHost()
    {
        await using var fake = await FakeConnection.OpenAsync();
        var pending = fake.Connection.RequestAsync(null, "fake.hang");
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, ControlFraming.MaxFrameSize + 1u);
        header[4] = 1;
        await fake.Host.WriteRawAsync(header);

        await Assert.ThrowsExactlyAsync<ControlProtocolException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.That(fake.Connection.IsClosed).IsTrue();
    }
}
