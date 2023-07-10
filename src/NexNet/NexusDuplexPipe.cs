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
public class NexusDuplexPipe : IDuplexPipe
{
    private readonly Pipe _inputPipe = new Pipe();
    private readonly PipeReaderWrapper _inputPipeReader;
    private readonly PipeWriterImpl _outputPipeWriter;

    private TaskCompletionSource? _readyTaskCompletionSource;

    private NexusDuplexPipeState _state = NexusDuplexPipeState.Unset;

    internal INexusSession? Session;

    internal ushort Id { get; private set; }

    private CancellationTokenRegistration _cancellationTokenRegistration;

    public PipeReader Input => _inputPipeReader;
    public PipeWriter Output => _outputPipeWriter;

    /// <summary>
    /// Task which completes upon the duplex pipe ready state;
    /// </summary>
    public Task Ready => _readyTaskCompletionSource.Task;

    public NexusDuplexPipe()
    {
        _inputPipeReader = new PipeReaderWrapper(this, _inputPipe.Reader);
        _outputPipeWriter = new PipeWriterImpl(this);
        _readyTaskCompletionSource = new TaskCompletionSource();
    }

    internal async ValueTask PipeReady(INexusSession session, ushort id)
    {
        if (Session == null)
            return;

        Id = id;

        UpdateState(Session.IsServer
            ? NexusDuplexPipeState.ServerReady
            : NexusDuplexPipeState.ClientReady);

        await NotifyState().ConfigureAwait(false);

        _outputPipeWriter.Setup(session, id);
        _readyTaskCompletionSource?.SetResult();
    }
    

    internal void Reset()
    {
        // Close everything.
        UpdateState(NexusDuplexPipeState.ClientWriterComplete
                            | NexusDuplexPipeState.ClientReaderComplete
                            | NexusDuplexPipeState.ServerReaderComplete
                            | NexusDuplexPipeState.ServerWriterComplete);

        _state = NexusDuplexPipeState.Unset;
        _inputPipe.Reset();
        _cancellationTokenRegistration.Dispose();
        // No need to reset anything with the writer as it is use once and dispose.
    }

    /*
    internal async Task RunWriter(object? arguments)
    {
        var runArguments = Unsafe.As<RunWriterArguments>(arguments)!;

        using var writer = new PipeWriterImpl(runArguments.InvocationId, runArguments.Session, runArguments.Logger);
        _pipeWriter = writer;
        
        await _writer!.Invoke(writer, runArguments.CancellationToken).ConfigureAwait(true);

        await writer.CompleteAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
        _pipeWriter = null;
    }
    */

    internal async ValueTask WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (Session == null)
            return;

        // See if this pipe is accepting data.
        if ((Session.IsServer && _state.HasFlag(NexusDuplexPipeState.ServerReaderComplete))
            || (!Session.IsServer && _state.HasFlag(NexusDuplexPipeState.ClientReaderComplete)))
            return;

        var length = (int)data.Length;
        data.CopyTo(_inputPipe.Writer.GetSpan(length));
        _inputPipe.Writer.Advance(length);
        
        var result = await _inputPipe.Writer.FlushAsync(_cancellationTokenRegistration.Token).ConfigureAwait(true);

        // Notify that the reader is completed.
        if (result.IsCompleted)
        {
            _state = Session.IsServer
                ? NexusDuplexPipeState.ServerReaderComplete
                : NexusDuplexPipeState.ClientReaderComplete;

            await NotifyState().ConfigureAwait(false);
        }
    }

    internal async ValueTask NotifyState()
    {
        if (Session == null)
            return;

        var message = Session.CacheManager.Rent<DuplexPipeUpdateStateMessage>();
        message.PipeId = Id;
        message.StateFlags = _state;
        await Session.SendMessage(message).ConfigureAwait(false);
        Session.CacheManager.Return(message);
    }

    internal void UpdateState(NexusDuplexPipeState updatedState)
    {
        if (Session == null)
            return;

        if (Session.IsServer)
        {
            
            if (updatedState.HasFlag(NexusDuplexPipeState.ClientReaderComplete)
                && !_state.HasFlag(NexusDuplexPipeState.ClientReaderComplete))
            {
                _state |= NexusDuplexPipeState.ClientReaderComplete | NexusDuplexPipeState.ServerWriterComplete;

                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
            }

            if (updatedState.HasFlag(NexusDuplexPipeState.ClientWriterComplete)
                && !_state.HasFlag(NexusDuplexPipeState.ClientWriterComplete))
            {
                _state |= NexusDuplexPipeState.ClientWriterComplete | NexusDuplexPipeState.ServerReaderComplete;

                // ReSharper disable once MethodHasAsyncOverload
                _inputPipe.Writer.Complete();
                _inputPipe.Writer.CancelPendingFlush();
            }
        }
        else
        {
            if (updatedState.HasFlag(NexusDuplexPipeState.ServerReaderComplete)
                && !_state.HasFlag(NexusDuplexPipeState.ServerReaderComplete))
            {
                _state |= NexusDuplexPipeState.ServerReaderComplete | NexusDuplexPipeState.ClientWriterComplete;

                // ReSharper disable once MethodHasAsyncOverload
                _inputPipe.Writer.Complete();
                _inputPipe.Writer.CancelPendingFlush();
            }

            if (updatedState.HasFlag(NexusDuplexPipeState.ServerWriterComplete)
                && !_state.HasFlag(NexusDuplexPipeState.ServerWriterComplete))
            {
                _state |= NexusDuplexPipeState.ServerWriterComplete | NexusDuplexPipeState.ClientReaderComplete;

                // ReSharper disable once MethodHasAsyncOverload
                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
            }
        }
    }
    /*
    internal void RegisterCancellationToken(CancellationToken token)
    {
        static void Cancel(object? pipeWriterObject)
        {
            var nexusPipe = Unsafe.As<NexusDuplexPipe>(pipeWriterObject)!;
            if (nexusPipe.Session == null)
                return;

            if(nexusPipe.Session.IsServer)
                nexusPipe.UpstreamUpdateState(NexusDuplexPipeState.ClientWriterComplete | );
        }

        _cancellationTokenRegistration = token.Register(Cancel, this);
    }*/

    private class PipeReaderWrapper : PipeReader
    {
        private readonly NexusDuplexPipe _nexusPipe;
        private readonly PipeReader _basePipe;

        public PipeReaderWrapper(NexusDuplexPipe nexusPipe, PipeReader basePipe)
        {
            _nexusPipe = nexusPipe;
            _basePipe = basePipe;
        }
        public override bool TryRead(out ReadResult result)
        {
            if (CanRead())
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, false, true);
                return false;
            }

            return _basePipe.TryRead(out result);
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            if (CanRead())
                return new ReadResult(ReadOnlySequence<byte>.Empty, false, true);

            try
            {
                return await _basePipe.ReadAsync(cancellationToken);
            }
            catch(Exception e)
            {
                _nexusPipe.Session?.Logger?.LogError(e, "NexusPipe Reading failure.");
                return new ReadResult(ReadOnlySequence<byte>.Empty, false, true);
            }
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            if (CanRead())
                return;

            _basePipe.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            if (CanRead())
                return;

            _basePipe.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead()
        {
            if (CanRead())
                return;

            _basePipe.CancelPendingRead();
        }

        public override void Complete(Exception? exception = null)
        {
            if (CanRead())
                return;

            _basePipe.Complete(exception);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanRead()
        {
            return _nexusPipe.Session.IsServer
                ? !_nexusPipe._state.HasFlag(NexusDuplexPipeState.ServerReaderComplete)
                : !_nexusPipe._state.HasFlag(NexusDuplexPipeState.ClientReaderComplete);
        }
    }

    internal record RunWriterArguments(
        int InvocationId, 
        INexusSession Session, 
        INexusLogger? Logger,
        CancellationToken CancellationToken);

    private class PipeWriterImpl : PipeWriter, IDisposable
    {
        private readonly NexusDuplexPipe _nexusDuplexPipe;
        private ushort _pipeId;
        private INexusSession _session;

        private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
        private bool _isCanceled;
        private bool _isCompleted;
        private CancellationTokenSource? _flushCts;
        private Memory<byte> _invocationIdBytes = new byte[4];
        private int _chunkSize;

        public PipeWriterImpl(NexusDuplexPipe nexusDuplexPipe)
        {
            _nexusDuplexPipe = nexusDuplexPipe;
        }

        public void Setup(INexusSession session, ushort pipeId)
        {
            _pipeId = pipeId;
            _session = session;
            _chunkSize = session.Config.PipeFlushChunkSize;

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
            static void CancelCallback(object? ctsObject)
            {
                Unsafe.As<CancellationTokenSource>(ctsObject)!.Cancel();
            }

            if (_flushCts?.IsCancellationRequested == true)
                return new FlushResult(_isCanceled, _isCompleted);

            _flushCts ??= new CancellationTokenSource();

            var bufferLength = _bufferWriter.Length;

            BitConverter.TryWriteBytes(_invocationIdBytes.Span, _pipeId);

            // Shortcut for empty buffer.
            if (bufferLength == 0)
                return new FlushResult(_isCanceled, _isCompleted);

            // ReSharper disable once UseAwaitUsing
            using var reg = cancellationToken.Register(CancelCallback, _flushCts);

            var buffer = _bufferWriter.GetBuffer();

            var multiPartSend = bufferLength > _chunkSize;

            var sendingBuffer = multiPartSend
                ? buffer.Slice(0, _chunkSize) 
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
                catch (InvalidOperationException)
                {
                    Complete();
                    break;
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    _session.Logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_pipeId}.");
                    await _session.DisconnectAsync(DisconnectReason.ProtocolError);
                    break;
                }

                bufferLength -= _chunkSize;
                if (bufferLength <= 0)
                    break;

                flushPosition += _chunkSize;

                sendingBuffer = buffer.Slice(flushPosition, Math.Min(bufferLength, _chunkSize));
            }

            _bufferWriter.Deallocate(buffer);

            // Try to reset the CTS.  If we can't just set it to null so a new one will be instanced.
            if (!_flushCts.TryReset())
                _flushCts = null;

            if (_isCompleted)
            {
                _nexusDuplexPipe.UpdateState(_session.IsServer
                    ? NexusDuplexPipeState.ServerWriterComplete
                    : NexusDuplexPipeState.ClientWriterComplete);

                await _nexusDuplexPipe.NotifyState();
            }

            return new FlushResult(_isCanceled, _isCompleted);
        }

        public void Dispose()
        {
            _bufferWriter.Dispose();
            _invocationIdBytes = null!;
        }
    }
}
