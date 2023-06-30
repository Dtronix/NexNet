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

public class NexNetPipe : IDisposable
{
    private readonly Pipe? _pipe;
    private PipeWriterImpl? _output;

    private int? _invocationId;
    private INexNetSession? _nexNetSession;
    private ResetAwaiterSource? _writerReadyTcs;

    public PipeReader Input
    {
        get
        {
            if (_pipe == null)
                throw new InvalidOperationException("Can't access the input from a writer.");
            return _pipe.Reader;
        }
    }


    public async ValueTask<PipeWriter> GetWriter()
    {
        if (_pipe != null)
            throw new InvalidOperationException("Can't access the output from a reader.");

        if (_output != null)
        {
            return _output;
        }

        _writerReadyTcs ??= new ResetAwaiterSource(false);

        await _writerReadyTcs.Awaiter;

        return _output!;
    }

    internal NexNetPipe(bool readerMode)
    {

        if (readerMode)
            _pipe = new Pipe();
    }

    public static NexNetPipe Create()
    {
        return new NexNetPipe(false);
    }

    public void Dispose()
    {
        Reset();
        _output?.Dispose();
    }

    internal void Reset()
    {
        _nexNetSession = null;
        _invocationId = null;
        _pipe?.Reset();

        // No need to reset anything with the writer as it is use once and dispose.
    }
    internal void Configure(int invocationId, INexNetSession nexNetSession)
    {
        _invocationId = invocationId;
        _nexNetSession = nexNetSession;
        _output = new PipeWriterImpl(invocationId, this._nexNetSession!);

        _writerReadyTcs?.TrySetResult();
    }

    internal void WriteFromStream(ReadOnlySequence<byte> data)
    {
        if (_pipe == null)
            throw new InvalidOperationException("Can't write to a non-reading pipe.");

        data.CopyTo(_pipe.Writer.GetSpan((int)data.Length));
    }

    private class PipeWriterImpl : PipeWriter, IDisposable
    {
        private readonly int _invocationId;
        private readonly INexNetSession _session;

        private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create();
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
            var data = _bufferWriter.Flush();
            using var flushData = _bufferWriter.Flush();

            BitConverter.TryWriteBytes(_invocationIdBytes.Span, _invocationId);

            try
            {
                await _session.SendHeaderWithBody(MessageType.PipeChannelWrite, _invocationIdBytes, flushData.Value, _flushCts.Token);
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
    /*
    private class PipeReaderImpl : PipeReader
    {
        private readonly NexNetPipe _pipe;
        public readonly ResetAwaiterSource ReceivedAwaiter = new ResetAwaiterSource(true);
        private bool _complete;
        private bool _cancel;
        public PipeReaderImpl(NexNetPipe pipe)
        {
            _pipe = pipe;
        }

        public void Reset()
        {
            _complete = false;
            _cancel = false;

            ReceivedAwaiter.Reset();
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            
            _pipe.BufferWriter.GetSequence()
            throw new NotImplementedException();
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            throw new NotImplementedException();

            
        }

        public override void CancelPendingRead()
        {
            _cancel = true;
            ReceivedAwaiter.TrySetCanceled();
        }

        public override void Complete(Exception? exception = null)
        {
            _complete = true;
            ReceivedAwaiter.TrySetResult();
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            if (_pipe.BufferWriter.Length == 0)
                await ReceivedAwaiter.Awaiter;

            if (_pipe.BufferWriter.Length == 0)
                return new ReadResult(new ReadOnlySequence<byte>(), _cancel, _complete);

            return new ReadResult(_pipe.BufferWriter.GetBuffer(), _cancel, _complete);
        }

        public override bool TryRead([UnscopedRef] out ReadResult result)
        {
            if (_pipe.BufferWriter.Length == 0)
            {
                result = new ReadResult(new ReadOnlySequence<byte>(), _cancel, _complete);
                return false;
            }

            result = new ReadResult(_pipe.BufferWriter.GetBuffer(), _cancel, _complete);

            return true;
        }
    }*/

}
