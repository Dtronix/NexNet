using NexNet.Pipes;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes;

internal class NexusChannelReaderWriterTests : NexusChannelReaderWriterTestBase
{
    [Test]
    public async Task WritesAndReadsData()
    {
        var (writer, reader, _, _) = GetChannelReaderWriter<ComplexMessage>();
        var value = ComplexMessage.Random();
        await writer.WriteAsync(value).Timeout(1);
        var result = await reader.ReadAsync().Timeout(1);
        Assert.That(result.Single(), Is.EqualTo(value));
    }

    [Test]
    public async Task WritesAndReadsMultipleData()
    {
        var (writer, reader, _, _) = GetChannelReaderWriter<ComplexMessage>();
        var iterations = 1000;
        var value = ComplexMessage.Random();
        for (var i = 0; i < iterations; i++)
        {
            await writer.WriteAsync(value).Timeout(1);
        }

        var result = await reader.ReadAsync().Timeout(1);
        foreach (var complexMessage in result)
        {
            Assert.That(complexMessage, Is.EqualTo(value));
        }

        Assert.That(result.Count(), Is.EqualTo(iterations));
    }
    
    /*
     TODO: Test this logic.
    [Test]
    public async Task ReadsAcrossMessageBoundary()
    {
        var (writer, reader, messenger, pipeReader) = GetChannelReaderWriter<ComplexMessage>();

        messenger.OnMessageSent = async (type, memory, arg3) =>
        {
            await pipeReader!.BufferData(arg3.Slice(0, 30)).Timeout(1);
        };
        
        var value = new ComplexMessage()
        {
            DateTime = DateTime.UnixEpoch,
            DateTimeOffset = DateTimeOffset.UnixEpoch,
            DateTimeOffsetNull = DateTimeOffset.UnixEpoch,
            Integer = 12345,
            String1 = "String1",
            StringNull = null
        };
        
        await writer.WriteAsync(value).Timeout(1);

        var result = await reader.ReadAsync().Timeout(1);
        //foreach (var complexMessage in result)
        //{
        //    Assert.That(complexMessage, Is.EqualTo(value));
        //}
//
        //Assert.That(result.Count(), Is.EqualTo(iterations));
    }*/
    
    [Repeat(20)]
    [Test]
    public async Task WritesAndReadsMultipleDataParallel()
    {
        var (writer, reader, _, _) = GetChannelReaderWriter<ComplexMessage>();
        var iterations = 10000;
        var value = ComplexMessage.Random();
        var count = 0;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                await writer.WriteAsync(value).Timeout(1);
            }
        });

        await Task.Run(async () =>
        {
            while (true)
            {
                var result = await reader.ReadAsync().Timeout(1);
                foreach (var complexMessage in result)
                {
                    Assert.That(complexMessage, Is.EqualTo(value));
                    count++;
                }

                if (count == iterations)
                {
                    break;
                }
            }
        }).Timeout(1);

        Assert.That(count, Is.EqualTo(iterations));
    }
    
    [Test]
    public async Task WritesAndReadsMultipleDataParallelSimpleMessage()
    {
        for (int x = 0; x < 100; x++)
        {
            var (writer, reader, _, _) = GetChannelReaderWriter<SimpleMessage>();
            var iterations = 1000;
            var value = new SimpleMessage()
            {
                Data = new byte[1 + x]
            };
            var count = 0;
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    await writer.WriteAsync(value).Timeout(1);
                }
            });

            await Task.Run(async () =>
            {
                var x2 = x;
                while (true)
                {
                    var result = await reader.ReadAsync().Timeout(1);
                    foreach (var simpleMessage in result)
                    {
                        Assert.That(simpleMessage.Data, Is.EqualTo(value.Data));
                        count++;
                    }

                    if (count == iterations)
                    {
                        break;
                    }
                }
            }).Timeout(1);

            Assert.That(count, Is.EqualTo(iterations));
        }
    }

    [Test]
    public async Task ReaderCompletesOnPartialRead()
    {
        var (writer, reader, _, _) = GetChannelReaderWriter<long>();
        var value = ComplexMessage.Random();

        _ = Task.Run(async () =>
        {
            await writer.Writer.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3, 4 }));
            await reader.Reader.CompleteAsync();
        });
        
        var completeRead = await reader.ReadUntilComplete().Timeout(1);

        Assert.That(completeRead.Count, Is.EqualTo(0));
    }
    private (NexusChannelWriter<T> writer, NexusChannelReader<T> reader, DummySessionMessenger sessionMessenger, NexusPipeReader pipeReader) GetChannelReaderWriter<T>()
    {
        var (pipeWriter, pipeReader, sessionMessenger) = GetConnectedPipeReaderWriter();

        var writer = new NexusChannelWriter<T>(pipeWriter);
        var reader = new NexusChannelReader<T>(pipeReader);

        return (writer, reader, sessionMessenger, pipeReader);
    }

}
