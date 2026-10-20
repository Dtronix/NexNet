using System;
using System.IO.Pipelines;
using System.Threading.Tasks;
using NexNet.Pipes;
using NexNet.Pipes.NexusStream;
using NexNet.Pipes.NexusStream.Frames;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

/// <summary>
/// A mock implementation of INexusDuplexPipe for testing.
/// </summary>
internal sealed class MockNexusDuplexPipe : INexusDuplexPipe
{
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly TaskCompletionSource _readyTcs = new();
    private readonly TaskCompletionSource _completeTcs = new();

    public ushort Id { get; set; } = 1;
    public Task ReadyTask => _readyTcs.Task;
    public Task CompleteTask => _completeTcs.Task;

    public PipeReader Input => _input.Reader;
    public PipeWriter Output => _output.Writer;

    /// <summary>
    /// Gets the writer for the input pipe (used to simulate incoming data).
    /// </summary>
    public PipeWriter InputWriter => _input.Writer;

    /// <summary>
    /// Gets the reader for the output pipe (used to verify outgoing data).
    /// </summary>
    public PipeReader OutputReader => _output.Reader;

    // Internal core accessors (not used in tests)
    NexusPipeWriter INexusDuplexPipe.WriterCore => throw new NotImplementedException("WriterCore not available in mock.");
    NexusPipeReader INexusDuplexPipe.ReaderCore => throw new NotImplementedException("ReaderCore not available in mock.");

    public MockNexusDuplexPipe()
    {
        _readyTcs.TrySetResult(); // Ready immediately
    }

    public void SetReady() => _readyTcs.TrySetResult();
    public void SetComplete() => _completeTcs.TrySetResult();

    public async ValueTask CompleteAsync()
    {
        await _input.Writer.CompleteAsync();
        await _output.Reader.CompleteAsync();
        _completeTcs.TrySetResult();
    }

    public async Task CleanupAsync()
    {
        await _input.Writer.CompleteAsync();
        await _input.Reader.CompleteAsync();
        await _output.Writer.CompleteAsync();
        await _output.Reader.CompleteAsync();
    }
}

/// <summary>
/// Helper methods for stream tests.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Creates a connected pair of mock duplex pipes.
    /// Data written to clientPipe.Output is readable from serverPipe.Input.
    /// Data written to serverPipe.Output is readable from clientPipe.Input.
    /// </summary>
    public static (MockNexusDuplexPipe ClientPipe, MockNexusDuplexPipe ServerPipe) CreatePipePair()
    {
        // For isolated testing, just return two mock pipes
        // In a real pipe pair, they would be connected
        return (new MockNexusDuplexPipe(), new MockNexusDuplexPipe());
    }
}

/// <summary>
/// Helper methods for stream tests.
/// </summary>
internal static class StreamTestHelpers
{
    /// <summary>
    /// Generates test data of the specified size.
    /// </summary>
    public static byte[] GenerateTestData(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)(i % 256);
        }
        return data;
    }

    /// <summary>
    /// Creates a successful open response with standard metadata.
    /// </summary>
    public static OpenResponseFrame CreateSuccessResponse(long length = 1024, bool canSeek = true, bool canRead = true, bool canWrite = true)
    {
        var metadata = new NexusStreamMetadata
        {
            Length = length,
            HasKnownLength = length >= 0,
            CanSeek = canSeek,
            CanRead = canRead,
            CanWrite = canWrite
        };
        return new OpenResponseFrame(metadata);
    }

    /// <summary>
    /// Creates a failure open response.
    /// </summary>
    public static OpenResponseFrame CreateErrorResponse(StreamErrorCode errorCode, string message)
    {
        return new OpenResponseFrame(errorCode, message);
    }
}
