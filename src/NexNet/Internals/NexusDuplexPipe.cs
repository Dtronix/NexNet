using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;

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
        ClientWriterServerReaderComplete = 1 << 0,

        /// <summary>
        /// Client reader has completed its operation.
        /// </summary>
        ClientReaderServerWriterComplete = 1 << 1,

        /// <summary>
        /// The connection is ready.
        /// </summary>
        Ready = 1 << 2,

        /// <summary>
        /// Client has reached it's buffer limit which notifies the server to stop sending data.
        /// </summary>
        ClientReaderBackPressure = 1 << 3,

        /// <summary>
        /// Server has reached it's buffer limit which notifies the client to stop sending data.
        /// </summary>
        ServerReaderBackPressure = 1 << 4,
        
        /// <summary>
        /// Marks the state to indicate that the communication session has completed its operation.
        /// </summary>
        Complete = ClientWriterServerReaderComplete
                   | ClientReaderServerWriterComplete
                   | Ready
    }

    //private readonly Pipe _inputPipe = new Pipe();
    private readonly PipeReaderImpl _inputPipeReader;
    private readonly PipeWriterImpl _outputPipeWriter;
    private TaskCompletionSource? _readyTcs;

    internal State CurrentState = State.Unset;

    private INexusSession? _session;

    // <summary>
    // Id which changes based upon completion of the pipe. Used to make sure the
    // Pipe is in the same state upon completion of writing/reading.
    // </summary>
    //internal int StateId;

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

    /// <summary>
    /// True if the pipe is in the cache.
    /// </summary>
    internal bool IsInCached = false;

    internal NexusDuplexPipe()
    {
        _inputPipeReader = new PipeReaderImpl(this);
        _outputPipeWriter = new PipeWriterImpl(this);
    }

    public ValueTask CompleteAsync()
    {
        var stateChanged = UpdateState(State.Complete);
        if (stateChanged)
            return NotifyState();

        _session?.CacheManager.NexusDuplexPipeCache.Return(this);

        return default;
    }

    /// <summary>
    /// Sets up the duplex pipe with the given parameters.
    /// </summary>
    /// <param name="initialId">The partial initial identifier.</param>
    /// <param name="session">The Nexus session.</param>
    /// <param name="onReady">The callback when the pipe is ready.</param>

    internal void Setup(byte initialId, INexusSession session, Func<INexusDuplexPipe, ValueTask>? onReady)
    {
        if (CurrentState != State.Unset)
            throw new InvalidOperationException("Can't setup a pipe that is already in use.");

        _readyTcs = new TaskCompletionSource();
        _onReady = onReady;
        _session = session;
        _logger = session.Logger;
        InitialId = initialId;
        _outputPipeWriter.Setup();
        _inputPipeReader.Setup();
    }


    /// <summary>
    /// Signals that the pipe is ready to receive and send messages.
    /// </summary>
    /// <param name="id">The ID of the pipe.</param>
    internal ValueTask PipeReady(ushort id)
    {
        if (_session == null)
            return default;

        Id = id;
        _outputPipeWriter.Setup();

        if (UpdateState(State.Ready))
            return NotifyState();

        return default;
    }
    
    /// <summary>
    /// Resets the pipe to its initial state while incrementing the StateId to indicate that the pipe has been reset.
    /// </summary>
    internal void Reset()
    {
        // Close everything.
        UpdateState(State.Complete);

        InitialId = 0;
        CurrentState = State.Unset;
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
    /// <returns>True if the data was written successfully, false if the pipe has reached its high water cutoff.</returns></returns>
    internal NexusPipeBufferResult WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_session == null)
            return NexusPipeBufferResult.DataIgnored;

        // See if this pipe is accepting data.
        if ((_session.IsServer && CurrentState.HasFlag(State.ClientWriterServerReaderComplete))
            || (!_session.IsServer && CurrentState.HasFlag(State.ClientReaderServerWriterComplete)))
            return NexusPipeBufferResult.DataIgnored;

        var bufferedLength = _inputPipeReader.BufferData(data);

        if (bufferedLength >= _session.Config.NexusPipeHighWaterCutoff)
        {
            _session.Logger?.LogWarning(
                $"Pipe {Id} has buffered {bufferedLength} bytes of data and exceed the high water cutoff of {_session.Config.NexusPipeHighWaterCutoff}");

            return NexusPipeBufferResult.HighCutoffReached;
        }
        else if (bufferedLength >= _session.Config.NexusPipeHighWaterMark)
        {
            _session.Logger?.LogInfo(
                $"Pipe {Id} has buffered {bufferedLength} bytes of data and exceed the high water of {_session.Config.NexusPipeHighWaterMark}");

            return NexusPipeBufferResult.HighWatermarkReached;
        }

        return NexusPipeBufferResult.Success;
    }

    internal async ValueTask NotifyState()
    {
        if(_session == null)
            return;

        var message = _session.CacheManager.Rent<DuplexPipeUpdateStateMessage>();
        message.PipeId = Id;
        message.State = CurrentState;
        await _session.SendMessage(message).ConfigureAwait(false);
        _session.CacheManager.Return(message);

        if (CurrentState == State.Complete)
            _session.CacheManager.NexusDuplexPipeCache.Return(this);
    }

    /// <summary>
    /// Updates the state of the NexusDuplexPipe .
    /// </summary>
    /// <param name="updatedState">The state to update to.</param>
    /// <returns>True if the state changed, false if it did not change.</returns>
    internal bool UpdateState(State updatedState, bool remove = false)
    {
        _logger?.LogTrace($"Current State: {CurrentState}; Update State: {updatedState}");
        if (_session == null || CurrentState.HasFlag(updatedState))
            return false;


        if (updatedState == State.Ready)
        {
            CurrentState = State.Ready;
            if (_onReady != null)
                TaskUtilities.StartTask<NexusDuplexPipe>(new(_onReady, this));

            // Set the ready task.
            _readyTcs?.TrySetResult();

            return true;
        }

        bool changed = false;

        if (HasState(updatedState, CurrentState, State.ClientReaderServerWriterComplete))
        {
            if (_session.IsServer)
            {
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }
            else
            {
                // Close input pipe.
                _inputPipeReader.Complete();
            }

            changed = true;
        }

        if (HasState(updatedState, CurrentState, State.ClientWriterServerReaderComplete))
        {
            if (_session.IsServer)
            {
                // Close input pipe.
                _inputPipeReader.Complete();

            }
            else
            {
                // Close output pipe.
                _outputPipeWriter.SetComplete();
                _outputPipeWriter.CancelPendingFlush();
            }

            changed = true;
        }

        // Back pressure
        if (HasState(updatedState, CurrentState, State.ClientReaderBackPressure) && _session.IsServer)
        {
            // Back pressure was added
            _outputPipeWriter.PauseWriting = true;
        }
        else if (remove && CurrentState.HasFlag(State.ClientReaderBackPressure)
                        && updatedState.HasFlag(State.ClientReaderBackPressure)
                        && _session.IsServer)
        {
            // Back pressure was removed.
            _outputPipeWriter.PauseWriting = false;
        }

        if (HasState(updatedState, CurrentState, State.ServerReaderBackPressure) && !_session.IsServer)
        {
            // Back pressure was added
            _outputPipeWriter.PauseWriting = true;
        }
        else if (remove && CurrentState.HasFlag(State.ServerReaderBackPressure)
                        && updatedState.HasFlag(State.ServerReaderBackPressure)
                        && !_session.IsServer)
        {
            // Back pressure was removed.
            _outputPipeWriter.PauseWriting = false;
        }

        if (remove)
        {          
            // Remove the state from the current state.
            CurrentState &= ~updatedState;
        }
        else
        {
            // Add the state to the current state.
            CurrentState |= updatedState;
        }

        return changed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasState(State has, State notHave, State flag)
    {
        return has.HasFlag(flag) && !notHave.HasFlag(flag);
    }

    internal class PipeReaderImpl : PipeReader
    {
        private readonly NexusDuplexPipe _nexusDuplexPipe;
        private readonly SemaphoreSlim _readSemaphore = new SemaphoreSlim(0, 1);
        private readonly CancellationRegistrationArgs _cancelReadingArgs;

        private record CancellationRegistrationArgs(SemaphoreSlim Semaphore);

        private readonly BufferWriter<byte> _buffer = BufferWriter<byte>.Create();

        private bool _isCompleted;
        private bool _isCanceled;
        private int _highWaterCutoff;
        private int _lowWaterMark;

        /// <summary>
        /// Length of data that has been buffered.
        /// </summary>
        public long BufferedLength
        {
            get
            {
                lock (_buffer) 
                    return _buffer.Length;
            }
        }

        public PipeReaderImpl(NexusDuplexPipe nexusDuplexPipe)
        {
            _nexusDuplexPipe = nexusDuplexPipe;
            _cancelReadingArgs = new CancellationRegistrationArgs(_readSemaphore);
        }

        public void Setup()
        {
            _highWaterCutoff = _nexusDuplexPipe._session!.Config.NexusPipeHighWaterCutoff;
            _lowWaterMark = _nexusDuplexPipe._session!.Config.NexusPipeLowWaterMark;
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
        /// <returns>The length of the buffered data.</returns>
        public long BufferData(ReadOnlySequence<byte> data)
        {
            long bufferLength;
            //using var lockToken = _readLock.TryWait(MutexSlim.WaitOptions.NoDelay);
            lock (_buffer)
            {
                var length = (int)data.Length;
                data.CopyTo(_buffer.GetSpan(length));
                _buffer.Advance(length);
                bufferLength = _buffer.Length;
            }

            //Interlocked.Increment(ref _stateId);
            ReleaseSemaphore(_readSemaphore);

            return bufferLength;
        }

        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            _isCompleted = true;

            var session = _nexusDuplexPipe._session;
            if (session == null)
                return default;

            if (_nexusDuplexPipe.UpdateState(session.IsServer
                    ? State.ClientWriterServerReaderComplete
                    : State.ClientReaderServerWriterComplete))
                return _nexusDuplexPipe.NotifyState();

            return default;
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
                await _readSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                //_lastReadStateId = _stateId;
            }
            catch (OperationCanceledException)
            {
                return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
            }
            finally
            {
                if (cts != null)
                    await cts.Value.DisposeAsync().ConfigureAwait(false);
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
            long bufferLength;
            lock (_buffer)
            {
                readOnlySequence = _buffer.GetBuffer();
                bufferLength = _buffer.Length;
            }

            var backPressureFlagCheck = _nexusDuplexPipe._session.IsServer
                ? State.ServerReaderBackPressure
                : State.ClientReaderBackPressure;

            // If we currently have back pressure, and the buffer length is below the low water mark, then we need to
            // notify the other side that we are ready to receive more data.
            if (_nexusDuplexPipe.CurrentState.HasFlag(backPressureFlagCheck) && bufferLength <= _lowWaterMark)
            {
                // Remove the flag and notify the other side.
                if(_nexusDuplexPipe.UpdateState(backPressureFlagCheck, true))
                    await _nexusDuplexPipe.NotifyState().ConfigureAwait(false);
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
        private readonly SemaphoreSlim _pauseSemaphore = new SemaphoreSlim(0, 1);
        private bool _pauseWriting;

        public PipeWriterImpl(NexusDuplexPipe nexusDuplexPipe)
        {
            _nexusDuplexPipe = nexusDuplexPipe;
        }

        /// <summary>
        /// Set to true to pause writing to the pipe.  The flush
        /// </summary>
        public bool PauseWriting
        {
            get => _pauseWriting;
            set
            {
                _pauseWriting = value;
                if(value == false)
                    _pauseSemaphore.Release();
            }
        }

        /// <summary>
        /// Sets up the pipe writer for use.
        /// </summary>
        public void Setup()
        {
            _chunkSize = _nexusDuplexPipe._session!.Config.NexusPipeFlushChunkSize;
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
            PauseWriting = false;

            // Reset the pause Semaphore back to 0.
            if (_pauseSemaphore.CurrentCount == 1)
                _pauseSemaphore.Wait();
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

            if(_nexusDuplexPipe.UpdateState(session.IsServer
                ? State.ClientReaderServerWriterComplete
                : State.ClientWriterServerReaderComplete))
                return _nexusDuplexPipe.NotifyState();

            return default;
        }

        public override void Complete(Exception? exception = null)
        {
            throw new InvalidOperationException("Use CompleteAsync instead.");
        }


        public void SetComplete()
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

            // Shortcut for empty buffer.
            if (bufferLength == 0)
                return new FlushResult(_isCanceled, _isCompleted);

            // ReSharper disable once UseAwaitUsing
            // TODO: Review only calling when the token can be canceled.
            using var reg = cancellationToken.Register(CancelCallback, _flushCts);

            if (PauseWriting)
                await _pauseSemaphore.WaitAsync(_flushCts.Token).ConfigureAwait(false);

            BitConverter.TryWriteBytes(_pipeId.Span, _nexusDuplexPipe.Id);

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
                        _flushCts.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // ReSharper disable once MethodHasAsyncOverload
                    SetComplete();
                    break;
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    _nexusDuplexPipe._logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_nexusDuplexPipe.Id}.");
                    await session.DisconnectAsync(DisconnectReason.ProtocolError).ConfigureAwait(false);
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
                if(_nexusDuplexPipe.UpdateState(session.IsServer
                    ? State.ClientReaderServerWriterComplete
                    : State.ClientWriterServerReaderComplete))
                    await _nexusDuplexPipe.NotifyState().ConfigureAwait(false);

                //-------------------------- await _nexusDuplexPipe.NotifyState();
            }

            return new FlushResult(_isCanceled, _isCompleted);
        }
    }
}
