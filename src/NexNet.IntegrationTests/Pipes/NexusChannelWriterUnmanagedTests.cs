using System.Buffers;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusChannelWriterUnmanagedTests : NexusChannelTestBase
{
    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase(65122584)]
    [TestCase((uint)616322584)]
    [TestCase(92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase('n')]
    [TestCase((float)9873571.1922)]
    [TestCase(9851512573571.198422)]
    public async Task WritesData<T>(T inputData)
        where T : unmanaged
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, ushort.MaxValue);
        var writer = new NexusChannelWriterUnmanaged<T>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();
        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return ValueTask.CompletedTask;
        };

        await writer.WriteAsync(inputData).Timeout(1);

        Assert.That(Utilities.GetValue<T>(bufferWriter.GetBuffer().ToArray()), Is.EqualTo(inputData));
    }

    [TestCase((sbyte)-54)]
    [TestCase((byte)200)]
    [TestCase((short)22584)]
    [TestCase((ushort)62584)]
    [TestCase(65122584)]
    [TestCase((uint)616322584)]
    [TestCase(92175120571057)]
    [TestCase((ulong)6163225235237523984)]
    [TestCase('n')]
    [TestCase((float)9873571.1922)]
    [TestCase(9851512573571.198422)]
    public async Task WritesDataWithPartialFlush<T>(T inputData)
        where T : unmanaged
    {
        var messenger = new DummySessionMessenger();
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, messenger, true, 1);

        var writer = new NexusChannelWriterUnmanaged<T>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();

        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return default;
        };

        await writer.WriteAsync(inputData).Timeout(1);

        Assert.That(Utilities.GetValue<T>(bufferWriter.GetBuffer().ToArray()), Is.EqualTo(inputData));
    }

    [Test]
    public async Task WritesCancels()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, new DummySessionMessenger(), true, ushort.MaxValue)
        {
            PauseWriting = true
        };

        var writer = new NexusChannelWriterUnmanaged<long>(nexusPipeWriter);

        var cts = new CancellationTokenSource(100);
        var writeResult = await writer.WriteAsync(123456789L, cts.Token).Timeout(1);

        Assert.That(writeResult, Is.False);
        Assert.That(writer.IsComplete, Is.False);
    }

    [Test]
    public async Task WritesCancelsImmediately()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), null, new DummySessionMessenger(), true, ushort.MaxValue)
        {
            PauseWriting = true
        };

        var writer = new NexusChannelWriterUnmanaged<long>(nexusPipeWriter);

        var cts = new CancellationTokenSource();
        cts.Cancel();
        var writeResult = await writer.WriteAsync(123456789L, cts.Token).Timeout(1);

        Assert.That(writeResult, Is.False);
        Assert.That(writer.IsComplete, Is.False);
    }
}
