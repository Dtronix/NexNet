using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Cache;
using NexNet.Internals;
using NexNet.Internals.Tasks;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet;

public class NexNetPipe
{
    private readonly Pipe? _pipe;
    private readonly WriterDelegate? _writer;

    public delegate Task WriterDelegate(PipeWriter writer, CancellationToken cancellationToken);

    public PipeReader Reader
    {
        get
        {
            if (_pipe == null)
                throw new InvalidOperationException("Can't access the input from a writer.");
            return _pipe.Reader;
        }
    }

    internal NexNetPipe()
    {
        _pipe = new Pipe();
    }

    private NexNetPipe(WriterDelegate writer)
    {
        _writer = writer;
    }

    public static NexNetPipe Create(WriterDelegate writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));

        return new NexNetPipe(writer);
    }

    internal void Reset()
    {
        _pipe?.Reset();

        // No need to reset anything with the writer as it is use once and dispose.
    }
    internal async Task RunWriter(object? arguments)
    {
        var runArguments = Unsafe.As<RunWriterArguments>(arguments)!;

        using var writer = new PipeWriterImpl(runArguments.InvocationId, runArguments.Session);

        await Task.Delay(1000);
        await _writer!.Invoke(writer, runArguments.CancellationToken);
    }

    internal async ValueTask WriteFromStream(ReadOnlySequence<byte> data)
    {
        if (_pipe == null)
            throw new InvalidOperationException("Can't write to a non-reading pipe.");
        var length = (int)data.Length;
        data.CopyTo(_pipe.Writer.GetSpan(length));
        _pipe.Writer.Advance(length);
        await _pipe.Writer.FlushAsync();
    }

    internal record RunWriterArguments(
        int InvocationId, 
        INexNetSession Session, 
        CancellationToken CancellationToken);

    private class PipeWriterImpl : PipeWriter, IDisposable
    {
        private readonly int _invocationId;
        private readonly INexNetSession _session;

        private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
        private bool _isCanceled;
        private bool _isCompleted;
        private CancellationTokenSource? _flushCts;
        private Memory<byte> _invocationIdBytes = new byte[4];

        public PipeWriterImpl(int invocationId, INexNetSession session)
        {
            _invocationId = invocationId;
            _session = session;
        }
    

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Advance(int bytes)
        {
            _bufferWriter.Advance(bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            return _bufferWriter.GetMemory(sizeHint);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            return _bufferWriter.GetSpan(sizeHint);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void CancelPendingFlush()
        {
            _isCanceled = true;
            _flushCts?.Cancel();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Complete(Exception? exception = null)
        {
            _isCompleted = true;
        }

        public override async ValueTask<FlushResult> FlushAsync(
            CancellationToken cancellationToken = new CancellationToken())
        {
            _flushCts ??= new CancellationTokenSource();

            cancellationToken.Register(() => _flushCts.Cancel());
            using var data = _bufferWriter.Flush();

            BitConverter.TryWriteBytes(_invocationIdBytes.Span, _invocationId);

            try
            {
                await _session.SendHeaderWithBody(MessageType.PipeChannelWrite, _invocationIdBytes, data.Value, _flushCts.Token);
            }
            catch (TaskCanceledException)
            {
                // noop
            }

            // Try to reset the CTS.  If we can't just set it to null so a new one will be instanced.
            if (!_flushCts.TryReset())
                _flushCts = null;

            return new FlushResult(_isCanceled, _isCompleted);
        }

        public void Dispose()
        {
            _bufferWriter.Dispose();
            _invocationIdBytes = null!;
        }
    }
}
