using System.Buffers;
using NexNet.Internals;
using NexNet.Messages;
using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusChannelReaderWriterUnmanagedTests : NexusChannelReaderWriterTestBase
{
    [Test]
    public async Task WritesAndReadsData()
    {
        var (writer, reader) = GetReaderWriter<long>();
        var value = 123456789L;
        await writer.WriteAsync(value).Timeout(1);
        var result = await reader.ReadAsync().Timeout(1);
        Assert.AreEqual(value, result.Single());
    }

    [Test]
    public async Task WritesAndReadsMultipleData()
    {
        var (writer, reader) = GetReaderWriter<long>();
        var iterations = 1000;
        for (var i = 0; i < iterations; i++)
        {
            await writer.WriteAsync(i).Timeout(1);
        }

        var count = 0L;
        var result = await reader.ReadAsync().Timeout(1);
        foreach (var l in result)
        {
            Assert.AreEqual(count++, l);
        }

        Assert.AreEqual(iterations, count);
    }


    [Test]
    public async Task WritesAndReadsMultipleDataParallel()
    {
        var (writer, reader) = GetReaderWriter<long>();
        var iterations = 10000;
        var count = 0;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await writer.WriteAsync(i).Timeout(1);
            }
        });

        await Task.Run(async () =>
        {
            while (true)
            {
                var result = await reader.ReadAsync().Timeout(1);
                foreach (var l in result)
                {
                    Assert.AreEqual(count++, l);
                }

                if (count == iterations)
                {
                    break;
                }
            }
        }).Timeout(1);

        Assert.AreEqual(iterations, count);

    }

    [Test]
    public async Task ReaderCompletesOnPartialRead()
    {
        var (writer, reader) = GetReaderWriter<long>();
        var value = ComplexMessage.Random();

        await writer.Writer.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3, 4 })).Timeout(1);
        await reader.Reader.CompleteAsync();

        var completeRead = await reader.ReadUntilComplete().Timeout(1);

        Assert.AreEqual(0, completeRead.Count);
    }

    private (NexusChannelWriterUnmanaged<T>, NexusChannelReaderUnmanaged<T>) GetReaderWriter<T>()
        where T : unmanaged
    {
        var (pipeWriter, pipeReader) = GetConnectedPipeReaderWriter();

        var writer = new NexusChannelWriterUnmanaged<T>(pipeWriter);
        var reader = new NexusChannelReaderUnmanaged<T>(pipeReader);

        return (writer, reader);
    }
}
