using System;
using System.IO;
using NexNet.Pipes.NexusStream;
using NUnit.Framework;

namespace NexNet.IntegrationTests.Pipes.NexusStream;

[TestFixture]
public class NexusStreamWrapperTests
{
    [Test]
    public void Constructor_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => new NexusStreamWrapper(null!));
    }

    [Test]
    public void InnerStream_ReturnsWrappedStream()
    {
        // Create a simple test stream adapter
        var mockStream = new TestNexusStream();
        using var wrapper = new NexusStreamWrapper(mockStream);

        Assert.That(wrapper.InnerStream, Is.SameAs(mockStream));
    }

    [Test]
    public void CanRead_DelegatesToInner()
    {
        var mockStream = new TestNexusStream { CanReadValue = true };
        using var wrapper = new NexusStreamWrapper(mockStream);

        Assert.That(wrapper.CanRead, Is.True);

        mockStream.CanReadValue = false;
        using var wrapper2 = new NexusStreamWrapper(mockStream);
        Assert.That(wrapper2.CanRead, Is.False);
    }

    [Test]
    public void CanWrite_DelegatesToInner()
    {
        var mockStream = new TestNexusStream { CanWriteValue = true };
        using var wrapper = new NexusStreamWrapper(mockStream);

        Assert.That(wrapper.CanWrite, Is.True);

        mockStream.CanWriteValue = false;
        using var wrapper2 = new NexusStreamWrapper(mockStream);
        Assert.That(wrapper2.CanWrite, Is.False);
    }

    [Test]
    public void CanSeek_DelegatesToInner()
    {
        var mockStream = new TestNexusStream { CanSeekValue = true };
        using var wrapper = new NexusStreamWrapper(mockStream);

        Assert.That(wrapper.CanSeek, Is.True);

        mockStream.CanSeekValue = false;
        using var wrapper2 = new NexusStreamWrapper(mockStream);
        Assert.That(wrapper2.CanSeek, Is.False);
    }

    [Test]
    public void Length_DelegatesToInner()
    {
        var mockStream = new TestNexusStream { LengthValue = 12345, HasKnownLengthValue = true };
        using var wrapper = new NexusStreamWrapper(mockStream);

        Assert.That(wrapper.Length, Is.EqualTo(12345));
    }

    [Test]
    public void Length_ThrowsWhenNoKnownLength()
    {
        var mockStream = new TestNexusStream { HasKnownLengthValue = false };
        using var wrapper = new NexusStreamWrapper(mockStream);

        Assert.Throws<NotSupportedException>(() => _ = wrapper.Length);
    }

    [Test]
    public void Position_Get_DelegatesToInner()
    {
        var mockStream = new TestNexusStream { PositionValue = 500 };
        using var wrapper = new NexusStreamWrapper(mockStream);

        Assert.That(wrapper.Position, Is.EqualTo(500));
    }

    [Test]
    public void Dispose_SetsDisposedState()
    {
        var mockStream = new TestNexusStream();
        var wrapper = new NexusStreamWrapper(mockStream);

        wrapper.Dispose();

        Assert.That(wrapper.CanRead, Is.False);
        Assert.That(wrapper.CanWrite, Is.False);
        Assert.That(wrapper.CanSeek, Is.False);
    }

    [Test]
    public void AfterDispose_ThrowsObjectDisposedException()
    {
        var mockStream = new TestNexusStream();
        var wrapper = new NexusStreamWrapper(mockStream);

        wrapper.Dispose();

        Assert.Throws<ObjectDisposedException>(() => wrapper.Flush());
        Assert.Throws<ObjectDisposedException>(() => wrapper.Read(new byte[10], 0, 10));
        Assert.Throws<ObjectDisposedException>(() => wrapper.Write(new byte[10], 0, 10));
        Assert.Throws<ObjectDisposedException>(() => wrapper.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => wrapper.SetLength(100));
    }

    /// <summary>
    /// Test implementation of INexusStream for wrapper testing.
    /// </summary>
    private class TestNexusStream : INexusStream
    {
        public NexusStreamState State { get; set; } = NexusStreamState.Open;
        public Exception? Error { get; set; }
        public long PositionValue { get; set; }
        public long Position => PositionValue;
        public long LengthValue { get; set; } = 1000;
        public long Length => LengthValue;
        public bool HasKnownLengthValue { get; set; } = true;
        public bool HasKnownLength => HasKnownLengthValue;
        public bool CanSeekValue { get; set; } = true;
        public bool CanSeek => CanSeekValue;
        public bool CanReadValue { get; set; } = true;
        public bool CanRead => CanReadValue;
        public bool CanWriteValue { get; set; } = true;
        public bool CanWrite => CanWriteValue;
        public Action<NexusStreamProgress>? OnProgress { get; set; }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken ct = default)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, System.Threading.CancellationToken ct = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<long> SeekAsync(long offset, SeekOrigin origin, System.Threading.CancellationToken ct = default)
        {
            return ValueTask.FromResult(offset);
        }

        public ValueTask FlushAsync(System.Threading.CancellationToken ct = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask SetLengthAsync(long length, System.Threading.CancellationToken ct = default)
        {
            LengthValue = length;
            return ValueTask.CompletedTask;
        }

        public ValueTask<NexusStreamMetadata> GetMetadataAsync(bool refresh = false, System.Threading.CancellationToken ct = default)
        {
            return ValueTask.FromResult(new NexusStreamMetadata
            {
                Length = LengthValue,
                HasKnownLength = HasKnownLengthValue,
                CanSeek = CanSeekValue,
                CanRead = CanReadValue,
                CanWrite = CanWriteValue
            });
        }

        public Stream GetStream()
        {
            return new NexusStreamWrapper(this);
        }

        public ValueTask DisposeAsync()
        {
            State = NexusStreamState.Closed;
            return ValueTask.CompletedTask;
        }
    }
}
