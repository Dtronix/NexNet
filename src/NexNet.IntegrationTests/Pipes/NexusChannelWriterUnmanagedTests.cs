using System.Buffers;
using NexNet.Pipes;
using NUnit.Framework;
using Pipelines.Sockets.Unofficial.Arenas;
using Pipelines.Sockets.Unofficial.Buffers;

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

        Assert.AreEqual(inputData, Utilities.GetValue<T>(bufferWriter.GetBuffer().ToArray()));
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
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), new ConsoleLogger(), messenger, true, 1);

        var writer = new NexusChannelWriterUnmanaged<T>(nexusPipeWriter);
        var bufferWriter = BufferWriter<byte>.Create();

        messenger.OnMessageSent = (type, header, body) =>
        {
            bufferWriter.Write(body.ToArray());
            return default;
        };

        await writer.WriteAsync(inputData).Timeout(1);

        Assert.AreEqual(inputData, Utilities.GetValue<T>(bufferWriter.GetBuffer().ToArray()));
    }

    [Test]
    public async Task WritesCancels()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), new ConsoleLogger(), new DummySessionMessenger(), true, ushort.MaxValue)
        {
            PauseWriting = true
        };

        var writer = new NexusChannelWriterUnmanaged<long>(nexusPipeWriter);

        var cts = new CancellationTokenSource(100);
        var writeResult = await writer.WriteAsync(123456789L, cts.Token).Timeout(1);

        Assert.IsFalse(writeResult);
        Assert.IsFalse(writer.IsComplete);
    }

    [Test]
    public async Task WritesCancelsImmediately()
    {
        var nexusPipeWriter = new NexusPipeWriter(new DummyPipeStateManager(), new ConsoleLogger(), new DummySessionMessenger(), true, ushort.MaxValue)
        {
            PauseWriting = true
        };

        var writer = new NexusChannelWriterUnmanaged<long>(nexusPipeWriter);

        var cts = new CancellationTokenSource();
        cts.Cancel();
        var writeResult = await writer.WriteAsync(123456789L, cts.Token).Timeout(1);

        Assert.IsFalse(writeResult);
        Assert.IsFalse(writer.IsComplete);
    }
}
