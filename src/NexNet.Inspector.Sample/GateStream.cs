namespace NexNet.Inspector.Sample;

/// <summary>
/// An in-memory recording destination whose writes can be held, to make a Drop-mode recorder fall behind on purpose:
/// while it holds, the recorder's sender waits in <see cref="Write(ReadOnlySpan{byte})"/>, its ring fills,
/// and the tap counts the ops that do not fit into a gap.
/// </summary>
public sealed class GateStream : Stream
{
    private readonly Lock _lock = new();
    private readonly MemoryStream _inner = new();
    private readonly ManualResetEventSlim _open = new(true);

    /// <summary>Holds later writes until <see cref="Release"/>.</summary>
    public void Hold() => _open.Reset();

    /// <summary>Releases held writes.</summary>
    public void Release() => _open.Set();

    /// <summary>Bytes written so far.</summary>
    public long Written
    {
        get
        {
            lock (_lock)
                return _inner.Length;
        }
    }

    /// <summary>
    /// Releases held writes and waits until no more than <paramref name="idleBytes"/> have been written in
    /// <paramref name="quiet"/>: the recorder has drained its ring, so the ops that follow fit again instead of
    /// extending the gap. A session that keeps pinging never goes fully quiet, hence the allowance.
    /// </summary>
    public async Task ReleaseAndDrainAsync(TimeSpan quiet, long idleBytes = 0, CancellationToken cancellationToken = default)
    {
        Release();
        var last = -1L;
        while (last < 0 || Written - last > idleBytes)
        {
            last = Written;
            await Task.Delay(quiet, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A copy of everything written so far.</summary>
    public byte[] ToArray()
    {
        lock (_lock)
            return _inner.ToArray();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _open.Wait();
        lock (_lock)
            _inner.Write(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Flush() => _open.Wait();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _open.Set();
        base.Dispose(disposing);
    }
}
