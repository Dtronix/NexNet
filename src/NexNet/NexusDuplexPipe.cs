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

internal interface ISetupNexusDuplexPipe
{
    public byte InitialId { get; }
}
/// <summary>
/// Pipe used for transmission of binary data from a one nexus to another.
/// </summary>
public class NexusDuplexPipe : ISetupNexusDuplexPipe, IDuplexPipe
{
    [Flags]
    internal enum State : byte
    {
        Unset = 0,
        ClientWriterComplete = 1 << 0,
        ClientReaderComplete = 1 << 1,
        ServerWriterComplete = 1 << 2,
        ServerReaderComplete = 1 << 3,
        ClientReady = 1 << 4,
        ServerReady = 1 << 5
    }

    private readonly Pipe _inputPipe = new Pipe();
    private readonly PipeReaderWrapper _inputPipeReader;
    private readonly PipeWriterImpl _outputPipeWriter;

    private State _state = State.Unset;

    private INexusSession? _session;

    internal ushort Id;

    private byte _initialId;
    byte ISetupNexusDuplexPipe.InitialId => _initialId;

    /// <summary>
    /// Gets the pipe reader for this connection.
    /// </summary>
    public PipeReader Input => _inputPipeReader;

    /// <summary>
    /// Gets the pipe writer for this connection.
    /// </summary>
    public PipeWriter Output => _outputPipeWriter;
    
    /// <summary>
    /// Method invoked on a LongRunning task when the pipe is ready for usage on the invoking side.
    /// </summary>
    public Func<IDuplexPipe, ValueTask>? OnReady { get; set; }

    internal NexusDuplexPipe()
    {
        _inputPipeReader = new PipeReaderWrapper(this, _inputPipe.Reader);
        _outputPipeWriter = new PipeWriterImpl(this);
    }

    internal void Setup(byte initialId, INexusSession session)
    {
        if (_state != State.Unset)
            throw new InvalidOperationException("Can't setup a pipe that is already in use.");

        _session = session;
        _initialId = initialId;
        _outputPipeWriter.Setup();
    }

    record class OnPipeReadyArguments(IDuplexPipe Pipe, Action<IDuplexPipe> InvokeAction);

    internal async ValueTask PipeReady(INexusSession session, ushort id)
    {
        if (_session == null)
            return;

        Id = id;

        UpdateState(_session.IsServer
            ? State.ServerReady
            : State.ClientReady);

        await NotifyState().ConfigureAwait(false);

        _outputPipeWriter.Setup();
    }

    internal void Reset()
    {
        // Close everything.
        UpdateState(State.ClientWriterComplete
                            | State.ClientReaderComplete
                            | State.ServerReaderComplete
                            | State.ServerWriterComplete);

        _initialId = 0;
        _state = State.Unset;
        _inputPipe.Reset();
        OnReady = null;
        // No need to reset anything with the writer as it is use once and dispose.
    }

    internal async ValueTask WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_session == null)
            return;

        // See if this pipe is accepting data.
        if ((_session.IsServer && _state.HasFlag(State.ServerReaderComplete))
            || (!_session.IsServer && _state.HasFlag(State.ClientReaderComplete)))
            return;

        var length = (int)data.Length;
        data.CopyTo(_inputPipe.Writer.GetSpan(length));
        _inputPipe.Writer.Advance(length);
        
        var result = await _inputPipe.Writer.FlushAsync().ConfigureAwait(true);

        // Notify that the reader is completed.
        if (result.IsCompleted)
        {
            _state = _session.IsServer
                ? State.ServerReaderComplete
                : State.ClientReaderComplete;

            await NotifyState().ConfigureAwait(false);
        }
    }

    internal async ValueTask NotifyState()
    {
        if (_session == null)
            return;
        
        var message = _session.CacheManager.Rent<DuplexPipeUpdateStateMessage>();
        message.PipeId = Id;
        message.State = _state;
        await _session.SendMessage(message).ConfigureAwait(false);
        _session.CacheManager.Return(message);
    }

    internal void UpdateState(State updatedState)
    {
        _session.Logger?.LogTrace($"Current State: {_state}; Update State: {updatedState}");
        if (_session == null || _state.HasFlag(updatedState))
            return;


        if (_session.IsServer)
        {
            if (updatedState == State.ClientReady)
            {
                if (OnReady != null)
                    TaskUtilities.StartTask<IDuplexPipe>(new(OnReady, this));
                return;
            }

            if (updatedState.HasFlag(State.ClientReaderComplete)
                && !_state.HasFlag(State.ClientReaderComplete))
            {
                _state |= State.ClientReaderComplete | State.ServerWriterComplete;

                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
            }

            if (updatedState.HasFlag(State.ClientWriterComplete)
                && !_state.HasFlag(State.ClientWriterComplete))
            {
                _state |= State.ClientWriterComplete | State.ServerReaderComplete;

                // Close entire input pipe.
                _inputPipe.Writer.Complete();
                _inputPipe.Writer.CancelPendingFlush();
                _inputPipe.Reader.Complete();
                _inputPipe.Reader.CancelPendingRead();
            }
        }
        else
        {

            if (updatedState == State.ServerReady)
            {
                if (OnReady != null)
                    TaskUtilities.StartTask<IDuplexPipe>(new(OnReady, this));
                return;
            }

            if (updatedState.HasFlag(State.ServerReaderComplete)
                && !_state.HasFlag(State.ServerReaderComplete))
            {
                _state |= State.ServerReaderComplete | State.ClientWriterComplete;

                // Close entire input pipe.
                _inputPipe.Writer.Complete();
                _inputPipe.Writer.CancelPendingFlush();
                _inputPipe.Reader.Complete();
                _inputPipe.Reader.CancelPendingRead();
            }

            if (updatedState.HasFlag(State.ServerWriterComplete)
                && !_state.HasFlag(State.ServerWriterComplete))
            {
                _state |= State.ServerWriterComplete | State.ClientReaderComplete;

                // ReSharper disable once MethodHasAsyncOverload
                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
            }
        }

        _state |= updatedState;
    }

    private class PipeReaderWrapper : PipeReader
    {
        private readonly NexusDuplexPipe _nexusPipe;
        private readonly PipeReader _basePipe;
        private static readonly ValueTask<ReadResult> _completeTask = ValueTask.FromResult(new ReadResult(ReadOnlySequence<byte>.Empty, false, true));
        
        public PipeReaderWrapper(NexusDuplexPipe nexusPipe, PipeReader basePipe)
        {
            _nexusPipe = nexusPipe;
            _basePipe = basePipe;
        }
        public override bool TryRead(out ReadResult result)
        {
            if (!CanRead())
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, false, true);
                return false;
            }

            return _basePipe.TryRead(out result);
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            if (!CanRead())
                return _completeTask;

            return _basePipe.ReadAsync(cancellationToken);
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            if (!CanRead())
                return;

            _basePipe.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            if (!CanRead())
                return;

            _basePipe.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead()
        {
            if (!CanRead())
                return;

            _basePipe.CancelPendingRead();
        }

        public override void Complete(Exception? exception = null)
        {
            if (!CanRead())
                return;

            _basePipe.Complete(exception);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanRead()
        {
            return _nexusPipe._session.IsServer
                ? !_nexusPipe._state.HasFlag(State.ServerReaderComplete)
                : !_nexusPipe._state.HasFlag(State.ClientReaderComplete);
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

        private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
        private bool _isCanceled;
        private bool _isCompleted;
        private CancellationTokenSource? _flushCts;
        private Memory<byte> _invocationIdBytes = new byte[sizeof(ushort)];
        private int _chunkSize;

        public PipeWriterImpl(NexusDuplexPipe nexusDuplexPipe)
        {
            _nexusDuplexPipe = nexusDuplexPipe;
        }

        public void Setup()
        {
            _chunkSize = _nexusDuplexPipe._session!.Config.PipeFlushChunkSize;
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

        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            _isCompleted = true;

            _nexusDuplexPipe.UpdateState(_nexusDuplexPipe._session.IsServer
                ? State.ServerWriterComplete
                : State.ClientWriterComplete);

            return _nexusDuplexPipe.NotifyState();

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

            BitConverter.TryWriteBytes(_invocationIdBytes.Span, _nexusDuplexPipe.Id);

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
            var session = _nexusDuplexPipe._session;
            while (true)
            {
                if(_isCanceled)
                    break;
                
                try
                {
                    await session.SendHeaderWithBody(
                        MessageType.DuplexPipeWrite,
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
                    session!.Logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_nexusDuplexPipe.Id}.");
                    await session.DisconnectAsync(DisconnectReason.ProtocolError);
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
                _nexusDuplexPipe.UpdateState(session.IsServer
                    ? State.ServerWriterComplete
                    : State.ClientWriterComplete);

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
