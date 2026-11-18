using NexNet.Pipes;

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
        await Assert.That(result.Single()).IsEqualTo(value);
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
            await Assert.That(complexMessage).IsEqualTo(value);
        }

        await Assert.That(result.Count()).IsEqualTo(iterations);
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
        //    await Assert.That(complexMessage).IsEqualTo(value);
        //}
//
        //await Assert.That(result.Count()).IsEqualTo(iterations);
    }*/
    
    [Repeat(19)] // TUnit's Repeat(n) runs n + 1 times; NUnit's ran n.
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
                    await Assert.That(complexMessage).IsEqualTo(value);
                    count++;
                }

                if (count == iterations)
                {
                    break;
                }
            }
        }).Timeout(1);

        await Assert.That(count).IsEqualTo(iterations);
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
                        await Assert.That(simpleMessage.Data).IsEquivalentTo(value.Data, CollectionOrdering.Matching);
                        count++;
                    }

                    if (count == iterations)
                    {
                        break;
                    }
                }
            }).Timeout(1);

            await Assert.That(count).IsEqualTo(iterations);
        }
    }

    [Test]
    public async Task ReaderCompletesOnPartialRead()
    {
        var (writer, reader, _, _) = GetChannelReaderWriter<long>();
        var value = ComplexMessage.Random();

        _ = Task.Run(async () =>
        {
            // A partial value: an int64 (0xd3) missing 5 of its 8 bytes.
            await writer.Writer.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 0xd3, 1, 2, 3 }));
            await reader.Reader.CompleteAsync();
        });
        
        var completeRead = await reader.ReadUntilComplete().Timeout(1);

        await Assert.That(completeRead.Count).IsEqualTo(0);
    }
    private (NexusChannelWriter<T> writer, NexusChannelReader<T> reader, DummySessionMessenger sessionMessenger, NexusPipeReader pipeReader) GetChannelReaderWriter<T>()
    {
        var (pipeWriter, pipeReader, sessionMessenger) = GetConnectedPipeReaderWriter();

        var writer = new NexusChannelWriter<T>(pipeWriter);
        var reader = new NexusChannelReader<T>(pipeReader);

        return (writer, reader, sessionMessenger, pipeReader);
    }

}