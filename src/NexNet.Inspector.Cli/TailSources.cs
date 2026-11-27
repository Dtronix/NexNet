using NexNet.Inspector.Core.Format;
using NexNet.Inspector.Core.Session;

namespace NexNet.Inspector.Cli;

/// <summary>
/// Several <see cref="LiveCapture"/>s over one block stream: a record-all stream or a growing multi-session recording.
/// Header and schema blocks are kept (copied) and replayed into each capture a session-open block starts; every other
/// block goes to the captures, each of which keeps only its own session's blocks. Fed from one thread; the sessions
/// are read from another under their gates.
/// </summary>
internal sealed class LiveCaptureSet
{
    private readonly List<NntlBlock> _prelude = [];
    private readonly List<LiveCapture> _captures = [];
    private readonly Lock _lock = new();

    /// <summary>True once the index footer arrived: the stream ended cleanly.</summary>
    public bool IsEnded { get; private set; }

    /// <summary>Feeds one block, in stream order.</summary>
    public LiveCaptureEvent Accept(in NntlBlock block)
    {
        switch (block.Kind)
        {
            case NntlBlockKind.Header:
            case NntlBlockKind.Schema:
                _prelude.Add(block with { Body = block.Body.ToArray() });
                foreach (var capture in _captures)
                    capture.Accept(block);
                return LiveCaptureEvent.Prepared;
            case NntlBlockKind.SessionOpen:
            {
                var capture = new LiveCapture();
                foreach (var prelude in _prelude)
                    capture.Accept(prelude);
                var opened = capture.Accept(block);
                if (capture.Session is not null)
                {
                    lock (_lock)
                        _captures.Add(capture);
                }

                return opened;
            }
            case NntlBlockKind.Index:
                IsEnded = true;
                return LiveCaptureEvent.StreamEnded;
            default:
                foreach (var capture in _captures)
                {
                    var changed = capture.Accept(block);
                    if (changed != LiveCaptureEvent.None)
                        return changed;
                }

                return LiveCaptureEvent.None;
        }
    }

    /// <summary>The stream stopped without its footer: sessions still open end truncated.</summary>
    public void Abandon()
    {
        foreach (var capture in _captures)
            capture.Abandon();
    }

    /// <summary>The sessions opened so far, in stream order.</summary>
    public void CopySessions(List<CaptureSession> into)
    {
        into.Clear();
        lock (_lock)
        {
            foreach (var capture in _captures)
                into.Add(capture.Session!);
        }
    }
}

/// <summary>Blocks in stream order: a live endpoint stream or a recording file read as it grows.</summary>
internal interface ITailSource : IAsyncDisposable
{
    NntlBlock Current { get; }

    ValueTask<bool> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>A recording read front to back while it grows (<c>tail --follow</c>), like <c>tail -f</c>.</summary>
internal sealed class FollowFileSource : ITailSource
{
    private readonly FileStream _file;
    private readonly System.IO.Pipelines.PipeReader _pipe;
    private readonly NntlPipeReader _reader;

    public FollowFileSource(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous);
        _pipe = System.IO.Pipelines.PipeReader.Create(new FollowStream(_file));
        _reader = new NntlPipeReader(_pipe);
    }

    public NntlBlock Current => _reader.Current;

    public ValueTask<bool> ReadAsync(CancellationToken cancellationToken) => _reader.ReadAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _pipe.CompleteAsync().ConfigureAwait(false);
        await _file.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A read-only stream that waits at the end of the file for more bytes instead of ending.</summary>
    private sealed class FollowStream(FileStream file) : Stream
    {
        private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0 || buffer.IsEmpty)
                    return read;
                await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
