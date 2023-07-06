using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet;
/// <summary>
/// Pipe used for transmission of binary data from a one nexus to another.
/// </summary>
public class NexusPipe
{
    private const int MaxFlushLength = 1 << 14;
    private readonly Pipe? _pipe;
    private readonly WriterDelegate? _writer;

    private CancellationTokenRegistration _cancellationTokenRegistration;
    internal INexusLogger? Logger;

    /// <summary>
    /// Delegate invoked when the nexus is ready to receive data.
    /// </summary>
    /// <param name="writer">Pipe writer for to fill with data to send to the remote nexus.</param>
    /// <param name="cancellationToken">Cancellation token invoked when writing is to complete.</param>
    /// <returns></returns>
    public delegate ValueTask WriterDelegate(PipeWriter writer, CancellationToken cancellationToken);

    /// <summary>
    /// Reader used by the nexus to read the data sent.
    /// </summary>
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

    /// <summary>
    /// Creates an instance of the NexusPipe class in writing mode.
    /// </summary>
    /// <param name="writer">Writer used to transmit the data to the nexus.</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException">Thrown when the writer delegate was not passed.</exception>
    public static NexusPipe Create(WriterDelegate writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));

        return new NexusPipe(writer);
    }

    internal void Reset()
    {
        _pipe?.Reset();
        _cancellationTokenRegistration.Dispose();
        // No need to reset anything with the writer as it is use once and dispose.
    }
    internal async Task RunWriter(object? arguments)
    {
        var runArguments = Unsafe.As<RunWriterArguments>(arguments)!;

        using var writer = new PipeWriterImpl(runArguments.InvocationId, runArguments.Session, runArguments.Logger);

        await _writer!.Invoke(writer, runArguments.CancellationToken).ConfigureAwait(true);

        await writer.CompleteAsync();
        await writer.FlushAsync();
    }

    internal void CompleteFromUpstream(PipeCompleteMessage.Flags completeFlags)
    {
        if (_pipe == null)
            throw new InvalidOperationException("Can't write to a non-reading pipe.");

        if (completeFlags.HasFlag(PipeCompleteMessage.Flags.Complete))
            _pipe.Writer.Complete();

        if(completeFlags.HasFlag(PipeCompleteMessage.Flags.Canceled))
            _pipe.Writer.CancelPendingFlush();
    }

    internal async ValueTask WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_pipe == null)
            throw new InvalidOperationException("Can't write to a non-reading pipe.");
        var length = (int)data.Length;
        data.CopyTo(_pipe.Writer.GetSpan(length));
        _pipe.Writer.Advance(length);
        
        await _pipe.Writer.FlushAsync(_cancellationTokenRegistration.Token).ConfigureAwait(true);
    }

    internal void RegisterCancellationToken(CancellationToken token)
    {
        static void Cancel(object? pipeWriterObject)
        {
            var writer = Unsafe.As<PipeWriter>(pipeWriterObject);
            writer!.Complete(new TaskCanceledException());
        }

        _cancellationTokenRegistration = token.Register(Cancel, _pipe!.Writer);
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


        public override void Complete(Exception? exception = null)
        {
            _isCompleted = true;
        }

        public override async ValueTask<FlushResult> FlushAsync(
            CancellationToken cancellationToken = new CancellationToken())
        {
            async ValueTask SendCompleteMessage()
            {
                var completeMessage = _session.CacheManager.PipeCompleteMessageDeserializer.Rent();
                completeMessage.InvocationId = _invocationId;
                completeMessage.CompleteFlags = PipeCompleteMessage.Flags.Unset;

                if(_isCompleted)
                    completeMessage.CompleteFlags |= PipeCompleteMessage.Flags.Complete;

                if (_isCanceled)
                    completeMessage.CompleteFlags |= PipeCompleteMessage.Flags.Canceled;

                // ReSharper disable once MethodSupportsCancellation
                await _session.SendHeaderWithBody(completeMessage).ConfigureAwait(false);

                _session.CacheManager.PipeCompleteMessageDeserializer.Return(completeMessage);
            }

            static void CancelCallback(object? ctsObject)
            {
                Unsafe.As<CancellationTokenSource>(ctsObject)!.Cancel();
            }

            _flushCts ??= new CancellationTokenSource();

            var bufferLength = _bufferWriter.Length;

            BitConverter.TryWriteBytes(_invocationIdBytes.Span, _invocationId);

            // Shortcut for empty buffer.
            if (bufferLength == 0)
            {
                if (_isCompleted || _isCanceled)
                    await SendCompleteMessage();

                return new FlushResult(_isCanceled, _isCompleted);
            }

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
                if(_isCanceled)
                    break;
                
                try
                {
                    await _session.SendHeaderWithBody(
                        MessageType.PipeWrite,
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

            if (_isCompleted || _isCanceled)
                await SendCompleteMessage();

            return new FlushResult(_isCanceled, _isCompleted);
        }

        public void Dispose()
        {
            _bufferWriter.Dispose();
            _invocationIdBytes = null!;
        }
    }
}
