using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamFrameWriterDataTests
{
    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }

    private Pipe _pipe = null!;
    private NexusStreamFrameWriter _writer = null!;
    private NexusStreamFrameReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _pipe = new Pipe();
        _writer = new NexusStreamFrameWriter(_pipe.Writer);
        _reader = new NexusStreamFrameReader(_pipe.Reader);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pipe.Writer.CompleteAsync();
        await _pipe.Reader.CompleteAsync();
    }

    [Test]
    public async Task WriteReadAsync_WritesCorrectFrame()
    {
        var frame = new ReadFrame(1024);

        await _writer.WriteReadAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Read));

        var parsed = NexusStreamFrameReader.ParseRead(result.Value.Payload);
        Assert.That(parsed.Count, Is.EqualTo(1024));
    }

    [Test]
    public async Task WriteWriteAsync_WritesCorrectFrame()
    {
        var frame = new WriteFrame(2048);

        await _writer.WriteWriteAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Write));

        var parsed = NexusStreamFrameReader.ParseWrite(result.Value.Payload);
        Assert.That(parsed.Count, Is.EqualTo(2048));
    }

    [Test]
    public async Task WriteWriteResponseAsync_Success_WritesCorrectFrame()
    {
        var frame = new WriteResponseFrame(1000, 5000);

        await _writer.WriteWriteResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.WriteResponse));

        var parsed = NexusStreamFrameReader.ParseWriteResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.True);
        Assert.That(parsed.BytesWritten, Is.EqualTo(1000));
        Assert.That(parsed.Position, Is.EqualTo(5000));
    }

    [Test]
    public async Task WriteWriteResponseAsync_Failure_WritesCorrectFrame()
    {
        var frame = new WriteResponseFrame(StreamErrorCode.DiskFull, 500);

        await _writer.WriteWriteResponseAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.WriteResponse));

        var parsed = NexusStreamFrameReader.ParseWriteResponse(result.Value.Payload);
        Assert.That(parsed.Success, Is.False);
        Assert.That(parsed.ErrorCode, Is.EqualTo(StreamErrorCode.DiskFull));
    }

    [Test]
    public async Task WriteDataAsync_WritesCorrectFrame()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var frame = new DataFrame(42, data);

        await _writer.WriteDataAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Data));

        var buffer = new byte[data.Length];
        var parsed = NexusStreamFrameReader.ParseData(result.Value.Payload, buffer);
        Assert.That(parsed.Sequence, Is.EqualTo(42));
        Assert.That(parsed.Data.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public async Task WriteDataEndAsync_WritesCorrectFrame()
    {
        var frame = new DataEndFrame(5000, 100);

        await _writer.WriteDataEndAsync(frame);

        var result = await _reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.DataEnd));

        var parsed = NexusStreamFrameReader.ParseDataEnd(result.Value.Payload);
        Assert.That(parsed.TotalBytes, Is.EqualTo(5000));
        Assert.That(parsed.FinalSequence, Is.EqualTo(100));
    }

    [Test]
    public async Task ChunkDataAsync_SingleChunk_ProducesOneFrame()
    {
        var sequenceManager = new SequenceManager();
        var data = new byte[100];

        var chunks = await ToListAsync(_writer.ChunkDataAsync(data, sequenceManager));

        Assert.That(chunks.Count, Is.EqualTo(1));
        Assert.That(chunks[0].Sequence, Is.EqualTo(0));
        Assert.That(chunks[0].Data.Length, Is.EqualTo(100));
    }

    [Test]
    public async Task ChunkDataAsync_ExactBoundary_ProducesOneFrame()
    {
        // Set max payload to a small value for testing
        var pipe = new Pipe();
        var writer = new NexusStreamFrameWriter(pipe.Writer, maxPayloadSize: 100 + DataFrame.HeaderSize);
        var sequenceManager = new SequenceManager();
        var data = new byte[100]; // Exactly max data size

        var chunks = await ToListAsync(writer.ChunkDataAsync(data, sequenceManager));

        Assert.That(chunks.Count, Is.EqualTo(1));
        Assert.That(chunks[0].Data.Length, Is.EqualTo(100));

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Test]
    public async Task ChunkDataAsync_MultipleChunks()
    {
        var pipe = new Pipe();
        var writer = new NexusStreamFrameWriter(pipe.Writer, maxPayloadSize: 50 + DataFrame.HeaderSize);
        var sequenceManager = new SequenceManager();
        var data = new byte[150]; // Will need 3 chunks of 50 bytes each

        var chunks = await ToListAsync(writer.ChunkDataAsync(data, sequenceManager));

        Assert.That(chunks.Count, Is.EqualTo(3));
        Assert.That(chunks[0].Sequence, Is.EqualTo(0));
        Assert.That(chunks[1].Sequence, Is.EqualTo(1));
        Assert.That(chunks[2].Sequence, Is.EqualTo(2));

        Assert.That(chunks[0].Data.Length, Is.EqualTo(50));
        Assert.That(chunks[1].Data.Length, Is.EqualTo(50));
        Assert.That(chunks[2].Data.Length, Is.EqualTo(50));

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Test]
    public async Task ChunkDataAsync_SequencesContinuous()
    {
        var pipe = new Pipe();
        var writer = new NexusStreamFrameWriter(pipe.Writer, maxPayloadSize: 20 + DataFrame.HeaderSize);
        var sequenceManager = new SequenceManager();

        // First chunking operation
        var data1 = new byte[50];
        var chunks1 = await ToListAsync(writer.ChunkDataAsync(data1, sequenceManager));

        // Second chunking operation - sequences should continue
        var data2 = new byte[50];
        var chunks2 = await ToListAsync(writer.ChunkDataAsync(data2, sequenceManager));

        // First operation: sequences 0, 1, 2 (3 chunks of 20, 20, 10)
        Assert.That(chunks1[0].Sequence, Is.EqualTo(0));
        Assert.That(chunks1[^1].Sequence, Is.EqualTo(2)); // Last chunk

        // Second operation: sequences continue from 3
        Assert.That(chunks2[0].Sequence, Is.EqualTo(3));

        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Test]
    public async Task ChunkDataAsync_EmptyData_ProducesNoChunks()
    {
        var sequenceManager = new SequenceManager();
        var data = new byte[0];

        var chunks = await ToListAsync(_writer.ChunkDataAsync(data, sequenceManager));

        Assert.That(chunks.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task WriteDataChunksUnlockedAsync_SmallData_SingleChunk()
    {
        var pipe = new Pipe();
        var writer = new NexusStreamFrameWriter(pipe.Writer, maxPayloadSize: 100 + DataFrame.HeaderSize);
        var reader = new NexusStreamFrameReader(pipe.Reader);
        var sequenceManager = new SequenceManager();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        await writer.WriteDataChunksUnlockedAsync(data, sequenceManager);
        await pipe.Writer.CompleteAsync();

        var result = await reader.ReadFrameAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Data));

        var buffer = new byte[data.Length];
        var parsed = NexusStreamFrameReader.ParseData(result.Value.Payload, buffer);
        Assert.That(parsed.Sequence, Is.EqualTo(0));
        Assert.That(parsed.Data.ToArray(), Is.EqualTo(data));

        await pipe.Reader.CompleteAsync();
    }

    [Test]
    public async Task WriteDataChunksUnlockedAsync_LargeData_MultipleChunks()
    {
        var pipe = new Pipe();
        var maxDataPerChunk = 20;
        var writer = new NexusStreamFrameWriter(pipe.Writer, maxPayloadSize: maxDataPerChunk + DataFrame.HeaderSize);
        var reader = new NexusStreamFrameReader(pipe.Reader);
        var sequenceManager = new SequenceManager();
        var data = StreamTestHelpers.GenerateTestData(50); // 3 chunks: 20, 20, 10

        await writer.WriteDataChunksUnlockedAsync(data, sequenceManager);
        await pipe.Writer.CompleteAsync();

        // Read all chunks
        var totalReceived = new byte[50];
        var offset = 0;

        for (int i = 0; i < 3; i++)
        {
            var result = await reader.ReadFrameAsync();
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.Header.Type, Is.EqualTo(FrameType.Data));

            NexusStreamFrameReader.ParseDataHeader(result.Value.Payload, out var seq, out var len);
            Assert.That(seq, Is.EqualTo((uint)i));

            var chunkBuffer = new byte[len];
            NexusStreamFrameReader.ParseData(result.Value.Payload, chunkBuffer);
            chunkBuffer.CopyTo(totalReceived, offset);
            offset += len;
        }

        Assert.That(totalReceived, Is.EqualTo(data));

        await pipe.Reader.CompleteAsync();
    }
}
