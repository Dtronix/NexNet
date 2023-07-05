using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet;

public class NexusPipe
{

    private const int MaxFlushLength = 1 << 14;
    private readonly Pipe? _pipe;
    private readonly WriterDelegate? _writer;

    internal INexusLogger? Logger;

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

    internal NexusPipe()
    {
        _pipe = new Pipe();
    }

    private NexusPipe(WriterDelegate writer)
    {
        _writer = writer;
    }

    public static NexusPipe Create(WriterDelegate writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));

        return new NexusPipe(writer);
    }

    internal void Reset()
    {
        _pipe?.Reset();

        // No need to reset anything with the writer as it is use once and dispose.
    }
    internal async Task RunWriter(object? arguments)
    {
        var runArguments = Unsafe.As<RunWriterArguments>(arguments)!;

        using var writer = new PipeWriterImpl(runArguments.InvocationId, runArguments.Session, runArguments.Logger);

        await _writer!.Invoke(writer, runArguments.CancellationToken).ConfigureAwait(true);
    }

    internal async ValueTask WriteFromStream(ReadOnlySequence<byte> data)
    {
        if (_pipe == null)
            throw new InvalidOperationException("Can't write to a non-reading pipe.");
        var length = (int)data.Length;
        data.CopyTo(_pipe.Writer.GetSpan(length));
        _pipe.Writer.Advance(length);
        await _pipe.Writer.FlushAsync().ConfigureAwait(true);
    }

    internal record RunWriterArguments(
        int InvocationId, 
        INexusSession Session, 
        INexusLogger? Logger,
        CancellationToken CancellationToken);

    private class PipeWriterImpl : PipeWriter, IDisposable
    {
        private readonly int _invocationId;
        private readonly INexusSession _session;
        private readonly INexusLogger? _logger;

        private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
        private bool _isCanceled;
        private bool _isCompleted;
        private CancellationTokenSource? _flushCts;
        private Memory<byte> _invocationIdBytes = new byte[4];

        public PipeWriterImpl(int invocationId, INexusSession session, INexusLogger? logger)
        {
            _invocationId = invocationId;
            _session = session;
            _logger = logger;
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
            static void CancelCallback(object? ctsObject)
            {
                Unsafe.As<CancellationTokenSource>(ctsObject)!.Cancel();
            }

            var bufferLength = _bufferWriter.Length;

            // Shortcut for empty buffer.
            if (bufferLength == 0)
                return new FlushResult(_isCanceled, _isCompleted);

            BitConverter.TryWriteBytes(_invocationIdBytes.Span, _invocationId);

            _flushCts ??= new CancellationTokenSource();

            // ReSharper disable once UseAwaitUsing
            using var reg = cancellationToken.Register(CancelCallback, _flushCts);

            var buffer = _bufferWriter.GetBuffer();

            var multiPartSend = bufferLength > NexusPipe.MaxFlushLength;

            var sendingBuffer = multiPartSend
                ? buffer.Slice(0, NexusPipe.MaxFlushLength) 
                : buffer;
            
            var flushPosition = 0;

            while (true)
            {
                try
                {
                    await _session.SendHeaderWithBody(
                        MessageType.PipeChannelWrite,
                        _invocationIdBytes,
                        sendingBuffer,
                        _flushCts.Token).ConfigureAwait(true);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    _logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_invocationId}.");
                    await _session.DisconnectAsync(DisconnectReason.ProtocolError);
                    break;
                }

                bufferLength -= MaxFlushLength;
                if (bufferLength <= 0)
                    break;

                flushPosition += MaxFlushLength;

                sendingBuffer = buffer.Slice(flushPosition, Math.Min(bufferLength, MaxFlushLength));
            }

            _bufferWriter.Deallocate(buffer);

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
