using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Arenas;
using Pipelines.Sockets.Unofficial.Buffers;
using Pipelines.Sockets.Unofficial.Threading;

namespace NexNet.Internals;

/// <summary>
/// Pipe used for transmission of binary data from a one nexus to another.
/// </summary>
internal class NexusDuplexPipeSlim : INexusDuplexPipe
{
    [Flags]
    internal enum State : byte
    {
        Unset = 0,
        ClientWriterComplete = 1 << 0,
        ClientReaderComplete = 1 << 1,
        ServerWriterComplete = 1 << 2,
        ServerReaderComplete = 1 << 3,
        Ready = 1 << 4,

        Complete = ClientWriterComplete 
                   | ClientReaderComplete 
                   | ServerWriterComplete
                   | ServerReaderComplete
                   | Ready
    }

    //private readonly Pipe _inputPipe = new Pipe();
    private readonly PipeReaderImpl _inputPipeReader;
    private readonly PipeWriterImpl _outputPipeWriter;

    private State _state = State.Unset;

    private INexusSession? _session;

    /// <summary>
    /// Id which changes based upon completion of the pipe. Used to make sure the
    /// Pipe is in the same state upon completion of writing/reading.
    /// </summary>
    internal int StateId = 0;

    public ushort Id { get; set; }

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

    private INexusLogger? _logger;

    internal NexusDuplexPipeSlim()
    {
        _inputPipeReader = new PipeReaderImpl();
        _outputPipeWriter = new PipeWriterImpl(this);
    }

    internal void Setup(byte initialId, INexusSession session, Func<INexusDuplexPipe, ValueTask>? onReady)
    {
        if (_state != State.Unset)
            throw new InvalidOperationException("Can't setup a pipe that is already in use.");

        _onReady = onReady;
        _session = session;
        _logger = session?.Logger;
        InitialId = initialId;
        _outputPipeWriter.Setup();
    }

    record class OnPipeReadyArguments(IDuplexPipe Pipe, Action<IDuplexPipe> InvokeAction);
    
    internal async ValueTask PipeReady(ushort id)
    {
        if (_session == null)
            return;

        Id = id;

        UpdateState(State.Ready);

        await NotifyState().ConfigureAwait(false);

        _outputPipeWriter.Setup();
    }
    
    internal void Reset()
    {
        StateId++;
        // Close everything.
        UpdateState(State.ClientWriterComplete
                            | State.ClientReaderComplete
                            | State.ServerReaderComplete
                            | State.ServerWriterComplete);

        InitialId = 0;
        _state = State.Unset;
        _onReady = null;
        // No need to reset anything with the writer as it is use once and dispose.
    }
    
    internal void WriteFromUpstream(ReadOnlySequence<byte> data)
    {
        if (_session == null)
            return;

        // See if this pipe is accepting data.
        if ((_session.IsServer && _state.HasFlag(State.ServerReaderComplete))
            || (!_session.IsServer && _state.HasFlag(State.ClientReaderComplete)))
            return;

        var length = (int)data.Length;
        _inputPipeReader.BufferData(data);
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
    
    private static async ValueTask FireOnReady(NexusDuplexPipeSlim pipe)
    {
        var state = pipe.StateId;
        var session = pipe._session;
        var logger = pipe._logger;

        if (session == null)
            return;

        try
        {
            await pipe._onReady!(pipe);
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Pipe did not successfully complete");
        }
        finally
        {
            if (pipe.StateId != state)
            {
                logger?.LogError("Could not return pipe to the PipeManager due to the state changing.  This is normally due to the manager cancellation process. Original state: New State:");
            }
            else
            {
                await session.PipeManager.ReturnPipe(pipe);
            }
        }
    }

    internal bool UpdateState(State updatedState)
    {
        _logger?.LogTrace($"Current State: {_state}; Update State: {updatedState}");
        if (_session == null || _state.HasFlag(updatedState))
            return false;

        if (updatedState == State.Ready)
        {
            _state = State.Ready;
            if (_onReady != null)
                TaskUtilities.StartTask<NexusDuplexPipeSlim>(new(FireOnReady, this));
            return true;
        }

        bool changed = false;
        if (_session.IsServer)
        {
            if (updatedState.HasFlag(State.ClientReaderComplete)
                && !_state.HasFlag(State.ClientReaderComplete))
            {
                _state |= State.ClientReaderComplete | State.ServerWriterComplete;

                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
                changed = true;
            }

            if (updatedState.HasFlag(State.ClientWriterComplete)
                && !_state.HasFlag(State.ClientWriterComplete))
            {
                _state |= State.ClientWriterComplete | State.ServerReaderComplete;

                // Close entire input pipe.
                //_inputPipe.Writer.Complete();
                //_inputPipe.Writer.CancelPendingFlush();
                //_inputPipe.Reader.Complete();
                //_inputPipe.Reader.CancelPendingRead();
                changed = true;
            }
        }
        else
        {
            if (updatedState.HasFlag(State.ServerReaderComplete)
                && !_state.HasFlag(State.ServerReaderComplete))
            {
                _state |= State.ServerReaderComplete | State.ClientWriterComplete;

                // Close entire input pipe.
                //_inputPipe.Writer.Complete();
                //_inputPipe.Writer.CancelPendingFlush();
                //_inputPipe.Reader.Complete();
                //_inputPipe.Reader.CancelPendingRead();
                changed = true;
            }

            if (updatedState.HasFlag(State.ServerWriterComplete)
                && !_state.HasFlag(State.ServerWriterComplete))
            {
                _state |= State.ServerWriterComplete | State.ClientReaderComplete;

                // ReSharper disable once MethodHasAsyncOverload
                _outputPipeWriter.Complete();
                _outputPipeWriter.CancelPendingFlush();
                changed = true;
            }
        }
        
        return changed;
    }

    internal class PipeReaderImpl : PipeReader
    {
        //private INexusSession? _session;
        
        //private MutexSlim _readLock = new MutexSlim(0);
        private readonly SemaphoreSlim _readSemaphore = new SemaphoreSlim(0, 1);
        private readonly CancellationRegistrationArgs _cancelReadingArgs;
        //private volatile int _stateId = 0;
        //private volatile int _lastReadStateId;

        private record CancellationRegistrationArgs(SemaphoreSlim Semaphore);

        private readonly BufferWriter<byte> _buffer = BufferWriter<byte>.Create();

        private bool _isComplete;
        private bool _isCanceled;
        
        public PipeReaderImpl()
        {
            _cancelReadingArgs = new CancellationRegistrationArgs(_readSemaphore);
        }
        public void Reset()
        {
            _isComplete = false;
            _isCanceled = false;

            lock (_buffer)
            {
                _buffer.Dispose();
            }
            

            //if (!_readLock.IsAvailable)
            //    throw new InvalidOperationException("ReadLock is still pending and can not reset.");

            // Reset the semaphore to it's original state.
            if (_readSemaphore.CurrentCount == 1)
                _readSemaphore.Wait();
        }

        public void BufferData(ReadOnlySequence<byte> data)
        {
            //using var lockToken = _readLock.TryWait(MutexSlim.WaitOptions.NoDelay);
            int oiriginalLength;
            lock (_buffer)
            {
                oiriginalLength = (int)data.Length;
                data.CopyTo(_buffer.GetSpan(oiriginalLength));
                _buffer.Advance(oiriginalLength);
            }
            //Thread.Sleep(1);
           //// Console.WriteLine($"Buffered {oiriginalLength} bytes. new Length: {_buffer.Length}");

            //Interlocked.Increment(ref _stateId);
            ReleaseSemaphore(_readSemaphore, 1);

        }

        public override bool TryRead(out ReadResult result)
        {
            result = default;
            return false;
        }


        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            if (_isComplete)
                return new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isComplete);

            if (cancellationToken.IsCancellationRequested)
            {
                var result = new ReadResult(ReadOnlySequence<byte>.Empty, true, _isComplete);
                return result;
            }

            if (_isCanceled)
            {
                _isCanceled = false;
                var result = new ReadResult(ReadOnlySequence<byte>.Empty, true, _isComplete);
                return result;
            }

            ReadOnlySequence<byte> readOnlySequence;

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
                    ReleaseSemaphore(args.Semaphore, 2);

                }, _cancelReadingArgs);
            }

            try
            {
               // Console.WriteLine("Waiting");

                await _readSemaphore.WaitAsync(cancellationToken);
                //// Console.WriteLine("Read Semaphore Passed");
                //// Console.WriteLine(_stateId == _lastReadStateId);
                //SpinWait.SpinUntil(() => _stateId != _lastReadStateId);


                // Console.WriteLine($"_lastReadStateId{_lastReadStateId} = _stateId{_stateId};");
                 //_lastReadStateId = _stateId;
            }
            catch (OperationCanceledException)
            {
                return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isComplete);
            }
            finally
            {
                if (cts != null)
                    await cts.Value.DisposeAsync();
            }

            //using var lockToken = await _readLock.TryWaitAsync(cancellationToken, MutexSlim.WaitOptions.NoDelay);

            //if (!lockToken.Success)
            //    return new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isComplete);

            if (_isComplete)
                return new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isComplete);

            if (_isCanceled)
            {
                _isCanceled = false;
                return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isComplete);
            }

            // Update the state Id;
            //_lastReadStateId = _stateId;

            lock (_buffer)
            {
                readOnlySequence = _buffer.GetBuffer();
               // Console.WriteLine($"Read {readOnlySequence.Length} bytes.");
            }

            return new ReadResult(readOnlySequence, cancellationToken.IsCancellationRequested, _isComplete);
        }

        public override void AdvanceTo(SequencePosition consumed)
        {
            //using var lockToken = _readLock.TryWait();
            lock (_buffer)
            {
                _buffer.ReleaseTo(consumed);
            }

        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            //using var lockToken = _readLock.TryWait();
            lock (_buffer)
            {
                _buffer.ReleaseTo(consumed);
            }
            // Ignore the examined as we don't have any provisions for that section of the data.
        }

        public override void CancelPendingRead()
        {
            _isCanceled = true;
            ReleaseSemaphore(_readSemaphore, 3);
        }

        public override void Complete(Exception? exception = null)
        {
            _isComplete = true;
            ReleaseSemaphore(_readSemaphore, 4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReleaseSemaphore(SemaphoreSlim semaphore, int source)
        {
            try
            {
                if (semaphore.CurrentCount == 0)
                {
                   // Console.WriteLine($"Releasing {source}");
                    semaphore.Release();
                   // Console.WriteLine($"Released {source}");

                }
                else
                {
                   // Console.WriteLine($"Already released {source}");
                }
            }
            catch
            {
                // ignore.
            }
        }

    }

    private class PipeWriterImpl : PipeWriter, IDisposable
    {
        private readonly NexusDuplexPipeSlim _nexusDuplexPipe;

        private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
        private bool _isCanceled;
        private bool _isCompleted;
        private CancellationTokenSource? _flushCts;
        private Memory<byte> _invocationIdBytes = new byte[sizeof(ushort)];
        private int _chunkSize;

        public PipeWriterImpl(NexusDuplexPipeSlim nexusDuplexPipe)
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

        /*
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            _isCompleted = true;

            _nexusDuplexPipe.UpdateState(_nexusDuplexPipe._session.IsServer
                ? State.ServerWriterComplete
                : State.ClientWriterComplete);

            return _nexusDuplexPipe.NotifyState();

        }*/

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

        public void Dispose()
        {
            _bufferWriter.Dispose();
            _invocationIdBytes = null!;
        }
    }
}
