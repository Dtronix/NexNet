using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;
using static System.Collections.Specialized.BitVector32;

namespace NexNet.Internals;

/// <summary>
/// Pipe used for transmission of binary data from a one nexus to another.
/// </summary>
internal class NexusDuplexPipe : INexusDuplexPipe
{
    /// <summary>
    /// Represents the state of a duplex pipe.
    /// </summary>
    [Flags]
    internal enum State : byte
    {
        /// <summary>
        /// Unset state.
        /// </summary>
        /// 
        Unset = 0,
        /// <summary>
        /// Client writer has completed its operation.
        /// </summary>
        ClientWriterComplete = 1 << 0,

        /// <summary>
        /// Client reader has completed its operation.
        /// </summary>
        ClientReaderComplete = 1 << 1,

        /// <summary>
        /// Server writer has completed its operation.
        /// </summary>
        ServerWriterComplete = 1 << 2,

        /// <summary>
        /// Server reader has completed its operation.
        /// </summary>
        ServerReaderComplete = 1 << 3,

        /// <summary>
        /// The connection is ready.
        /// </summary>
        Ready = 1 << 4,

        /// <summary>
        /// Marks the state to indicate that the communication session has completed its operation.
        /// </summary>
        Complete = ClientWriterComplete
                   | ClientReaderComplete
                   | ServerWriterComplete
                   | ServerReaderComplete
                   | Ready
    }

    //private readonly Pipe _inputPipe = new Pipe();
    private readonly PipeReaderImpl _inputPipeReader;
    private readonly PipeWriterImpl _outputPipeWriter;
    private TaskCompletionSource? _readyTcs;

    private State _state = State.Unset;

    private INexusSession? _session;

    /// <summary>
    /// Id which changes based upon completion of the pipe. Used to make sure the
    /// Pipe is in the same state upon completion of writing/reading.
    /// </summary>
    internal int StateId;

    /// <summary>
    /// Complete compiled ID containing the client bit and the server bit.
    /// </summary>
    public ushort Id { get; set; }

    /// <summary>
    /// Initial ID of this side of the connection.
    /// </summary>
    public byte InitialId { get; set; }

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
    private Func<INexusDuplexPipe, ValueTask>? _onReady;

    /// <summary>
    /// Task which completes when the pipe is ready for usage on the invoking side.
    /// </summary>
    public Task ReadyTask => _readyTcs!.Task;

    /// <summary>
    /// Logger.
    /// </summary>
    private INexusLogger? _logger;

    internal NexusDuplexPipe()
    {
        _inputPipeReader = new PipeReaderImpl(this);
        _outputPipeWriter = new PipeWriterImpl(this);
    }

    /// <summary>
    /// Sets up the duplex pipe with the given parameters.
    /// </summary>
    /// <param name="initialId">The partial initial identifier.</param>
    /// <param name="session">The Nexus session.</param>
    /// <param name="onReady">The callback when the pipe is ready.</param>

    internal void Setup(byte initialId, INexusSession session, Func<INexusDuplexPipe, ValueTask>? onReady)
    {
        if (_state != State.Unset)
            throw new InvalidOperationException("Can't setup a pipe that is already in use.");

        _readyTcs = new TaskCompletionSource();
        _onReady = onReady;
        _session = session;
        _logger = session.Logger;
        InitialId = initialId;
        _outputPipeWriter.Setup();
    }


    /// <summary>
    /// Signals that the pipe is ready to receive and send messages.
    /// </summary>
    /// <param name="id">The ID of the pipe.</param>
    internal async ValueTask PipeReady(ushort id)
    {
        if (_session == null)
            return;

        Id = id;

        await UpdateState(State.Ready).ConfigureAwait(false);

        _outputPipeWriter.Setup();
    }
    
    /// <summary>
    /// Resets the pipe to its initial state while incrementing the StateId to indicate that the pipe has been reset.
    /// </summary>
    internal void Reset()
    {
        StateId++;
        // Close everything.
        UpdateState(State.Complete);

        InitialId = 0;
        _state = State.Unset;
        _onReady = null;

        // Set the task to canceled in case the pipe was reset before it was ready.
        _readyTcs?.TrySetCanceled();
        _readyTcs = null;

        _inputPipeReader.Reset();
        _outputPipeWriter.Reset();
        // No need to reset anything with the writer as it is use once and dispose.
    }

    /// <summary>
    /// Writes data from upstream reader into the input pipe
    /// </summary>
    /// <param name="data">The data to write from upstream reader</param>
    internal void WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_session == null)
            return;

        // See if this pipe is accepting data.
        if ((_session.IsServer && _state.HasFlag(State.ServerReaderComplete))
            || (!_session.IsServer && _state.HasFlag(State.ClientReaderComplete)))
            return;

        _inputPipeReader.BufferData(data);
    }

    /// <summary>
    /// Updates the state of the NexusDuplexPipe .
    /// </summary>
    /// <param name="updatedState">The state to update to.</param>
    /// <returns>True if the state changed, false if it did not change.</returns>

    internal async ValueTask UpdateState(State updatedState)
    {
        _logger?.LogTrace($"Current State: {_state}; Update State: {updatedState}");
        if (_session == null || _state.HasFlag(updatedState))
            return;

        if (updatedState == State.Ready)
        {
            _state = State.Ready;
            if (_onReady != null)
                TaskUtilities.StartTask<NexusDuplexPipe>(new(_onReady, this));

            // Set the ready task.
            _readyTcs?.TrySetResult();

            return;
        }

        bool changed = false;

        if (_session.IsServer)
        {
            if ((updatedState.HasFlag(State.ClientReaderComplete) && !_state.HasFlag(State.ClientReaderComplete))
                || (updatedState.HasFlag(State.ServerWriterComplete) && !_state.HasFlag(State.ServerWriterComplete)))
            {
                _state |= State.ClientReaderComplete | State.ServerWriterComplete;

                //_logger?.LogTrace($"NexusDuplexPipe Writer Closed ----------------");
                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
                changed = true;
            }

            if ((updatedState.HasFlag(State.ClientWriterComplete) && !_state.HasFlag(State.ClientWriterComplete))
                || (updatedState.HasFlag(State.ServerReaderComplete) && !_state.HasFlag(State.ServerReaderComplete)))
            {
                _state |= State.ClientWriterComplete | State.ServerReaderComplete;

                // Close input pipe.
                _inputPipeReader.Complete();
                changed = true;
            }
        }
        else
        {
            if ((updatedState.HasFlag(State.ClientReaderComplete) && !_state.HasFlag(State.ClientReaderComplete))
                || (updatedState.HasFlag(State.ServerWriterComplete) && !_state.HasFlag(State.ServerWriterComplete)))
            {
                _state |= State.ClientReaderComplete | State.ServerWriterComplete;
                // Close input pipe.
                _inputPipeReader.Complete();
                changed = true;

            }

            if ((updatedState.HasFlag(State.ServerReaderComplete) && !_state.HasFlag(State.ServerReaderComplete))
                || (updatedState.HasFlag(State.ClientWriterComplete) && !_state.HasFlag(State.ClientWriterComplete)))
            {
                _state |= State.ClientWriterComplete | State.ServerReaderComplete;

                // Close output pipe.
                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
                changed = true;
            }
        }

        if(changed)
        {
            var message = _session.CacheManager.Rent<DuplexPipeUpdateStateMessage>();
            message.PipeId = Id;
            message.State = _state;
            await _session.SendMessage(message).ConfigureAwait(false);
            _session.CacheManager.Return(message);

            if (_state == State.Complete)
                _session.CacheManager.NexusDuplexPipeCache.Return(this);
        }
    }

    internal class PipeReaderImpl : PipeReader
    {
        private readonly NexusDuplexPipe _nexusDuplexPipe;
        private readonly SemaphoreSlim _readSemaphore = new SemaphoreSlim(0, 1);
        private readonly CancellationRegistrationArgs _cancelReadingArgs;
        //private volatile int _stateId = 0;
        //private volatile int _lastReadStateId;

        private record CancellationRegistrationArgs(SemaphoreSlim Semaphore);

        private readonly BufferWriter<byte> _buffer = BufferWriter<byte>.Create();

        private bool _isCompleted;
        private bool _isCanceled;
        
        public PipeReaderImpl(NexusDuplexPipe nexusDuplexPipe)
        {
            _nexusDuplexPipe = nexusDuplexPipe;
            _cancelReadingArgs = new CancellationRegistrationArgs(_readSemaphore);
        }

        /// <summary>
        /// Resets the reader to it's initial state for reuse.
        /// </summary>
        public void Reset()
        {
            _isCompleted = false;
            _isCanceled = false;

            lock (_buffer)
            {
                _buffer.Dispose();
            }

            // Reset the semaphore to it's original state.
            if (_readSemaphore.CurrentCount == 1)
                _readSemaphore.Wait();
        }

        /// <summary>
        /// Buffers incoming data to the reader and notifies the reader that data is available.
        /// </summary>
        /// <param name="data">The incoming data as a ReadOnlySequence of bytes.</param>
        public void BufferData(ReadOnlySequence<byte> data)
        {
            //using var lockToken = _readLock.TryWait(MutexSlim.WaitOptions.NoDelay);
            lock (_buffer)
            {
                var length = (int)data.Length;
                data.CopyTo(_buffer.GetSpan(length));
                _buffer.Advance(length);
            }

            //Interlocked.Increment(ref _stateId);
            ReleaseSemaphore(_readSemaphore);

        }

        public override async ValueTask CompleteAsync(Exception? exception = null)
        {
            _isCompleted = true;

            var session = _nexusDuplexPipe._session;
            if (session == null)
                return;

            await _nexusDuplexPipe.UpdateState(session.IsServer
                ? State.ServerReaderComplete
                : State.ClientReaderComplete);
        }


        public override bool TryRead(out ReadResult result)
        {
            if (_isCompleted)
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isCompleted);
                return false;
            }

            if (_isCanceled)
            {
                _isCanceled = false;
                result = new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
                return false;
            }

            try
            {
                _readSemaphore.Wait();
            }
            catch (OperationCanceledException)
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
                return false;
            }

            if (_isCompleted)
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isCompleted);
                return false;
            }

            if (_isCanceled)
            {
                _isCanceled = false;
                result = new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
                return false;
            }

            // Update the state Id;
            //_lastReadStateId = _stateId;

            ReadOnlySequence<byte> readOnlySequence;
            lock (_buffer)
            {
                readOnlySequence = _buffer.GetBuffer();
            }

            result = new ReadResult(readOnlySequence, false, _isCompleted);
            return true;
        }


        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            if (_isCompleted)
                return new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isCompleted);

            if (cancellationToken.IsCancellationRequested)
                return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);

            if (_isCanceled)
            {
                _isCanceled = false;
                var result = new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
                return result;
            }
            
            // Compare the state id to the last read state id. If they are different, then the state has changed
            // and we need to return the current buffer.
            // TODO: Investigate this hotpath.
            /*if (_lastReadStateId != _stateId)
            {

               // Console.WriteLine("State Changed Hotpath");
                // Consume the writer.
                if (_readSemaphore.CurrentCount > 0)
                { 
                    _readSemaphore.Wait();
                    _lastReadStateId = _stateId;
                }


                lock (_buffer)
                {
                    readOnlySequence = _buffer.GetBuffer();
                }
                return new ReadResult(readOnlySequence, cancellationToken.IsCancellationRequested, _isComplete);
            }*/

            CancellationTokenRegistration? cts = null;

            if (cancellationToken.CanBeCanceled)
            {
                cts = cancellationToken.UnsafeRegister(static argsObj =>
                {
                    var args = Unsafe.As<CancellationRegistrationArgs>(argsObj)!;
                    ReleaseSemaphore(args.Semaphore);

                }, _cancelReadingArgs);
            }

            try
            {
                await _readSemaphore.WaitAsync(cancellationToken);
                //_lastReadStateId = _stateId;
            }
            catch (OperationCanceledException)
            {
                return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
            }
            finally
            {
                if (cts != null)
                    await cts.Value.DisposeAsync();
            }

            //using var lockToken = await _readLock.TryWaitAsync(cancellationToken, MutexSlim.WaitOptions.NoDelay);

            //if (!lockToken.Success)
            //    return new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isComplete);

            if (_isCompleted)
                return new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isCompleted);

            if (_isCanceled)
            {
                _isCanceled = false;
                return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
            }

            // Update the state Id;
            //_lastReadStateId = _stateId;

            ReadOnlySequence<byte> readOnlySequence;
            lock (_buffer)
            {
                readOnlySequence = _buffer.GetBuffer();
            }

            return new ReadResult(readOnlySequence, cancellationToken.IsCancellationRequested, _isCompleted);
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            lock (_buffer)
            {
                _buffer.ReleaseTo(consumed);
            }
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            lock (_buffer)
            {
                _buffer.ReleaseTo(consumed);
            }
        }

        public override void CancelPendingRead()
        {
            _isCanceled = true;
            ReleaseSemaphore(_readSemaphore);
        }

        public override void Complete(Exception? exception = null)
        {
            _isCompleted = true;
            ReleaseSemaphore(_readSemaphore);
        }

        /// <summary>
        /// Releases the semaphore if it is currently held.
        /// </summary>
        /// <param name="semaphore"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReleaseSemaphore(SemaphoreSlim semaphore)
        {
            try
            {
                if (semaphore.CurrentCount == 0)
                {
                    semaphore.Release();

                }
                else
                {
                }
            }
            catch
            {
                // ignore.
            }
        }

    }

    private class PipeWriterImpl : PipeWriter
    {
        private readonly NexusDuplexPipe _nexusDuplexPipe;

        private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
        private bool _isCanceled;
        private bool _isCompleted;
        private CancellationTokenSource? _flushCts;
        private Memory<byte> _pipeId = new byte[sizeof(ushort)];
        private int _chunkSize;
        
        public PipeWriterImpl(NexusDuplexPipe nexusDuplexPipe)
        {
            _nexusDuplexPipe = nexusDuplexPipe;
        }

        /// <summary>
        /// Sets up the pipe writer for use.
        /// </summary>
        public void Setup()
        {
            _chunkSize = _nexusDuplexPipe._session!.Config.PipeFlushChunkSize;
        }

        /// <summary>
        /// Resets the pipe writer for reuse.
        /// </summary>
        public void Reset()
        {
            _bufferWriter.Dispose();
            _isCanceled = false;
            _isCompleted = false;
            _flushCts?.Dispose();
            _flushCts = null;
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

            var session = _nexusDuplexPipe._session;
            if (session == null)
                return default;

            return _nexusDuplexPipe.UpdateState(session.IsServer
                ? State.ServerWriterComplete
                : State.ClientWriterComplete);
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

            BitConverter.TryWriteBytes(_pipeId.Span, _nexusDuplexPipe.Id);

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

            if (session == null)
                throw new InvalidOperationException("Session is null.");

            while (true)
            {
                if(_isCanceled)
                    break;
                
                try
                {
                    await session.SendHeaderWithBody(
                        MessageType.DuplexPipeWrite,
                        _pipeId,
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
                    _nexusDuplexPipe._logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_nexusDuplexPipe.Id}.");
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

                //-------------------------- await _nexusDuplexPipe.NotifyState();
            }

            return new FlushResult(_isCanceled, _isCompleted);
        }
    }
}
