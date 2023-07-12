﻿using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
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

    internal NexusDuplexPipe()
    {
        _inputPipeReader = new PipeReaderImpl(this, _inputPipe.Reader);
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
        _inputPipe.Reset();
        _onReady = null;
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

    private static async ValueTask FireOnReady(NexusDuplexPipe pipe)
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
                TaskUtilities.StartTask<NexusDuplexPipe>(new(FireOnReady, this));
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
                _inputPipe.Writer.Complete();
                _inputPipe.Writer.CancelPendingFlush();
                _inputPipe.Reader.Complete();
                _inputPipe.Reader.CancelPendingRead();
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
                _inputPipe.Writer.Complete();
                _inputPipe.Writer.CancelPendingFlush();
                _inputPipe.Reader.Complete();
                _inputPipe.Reader.CancelPendingRead();
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

    private class PipeReaderImpl : PipeReader
    {
        private readonly NexusDuplexPipe _nexusPipe;

        public readonly BufferWriter<byte> Buffer;

        private MutexSlim _readLock = new MutexSlim(0);

        private bool _isComplete;
        private bool _isCanceled;

        private static readonly ValueTask<ReadResult> _completeTask = ValueTask.FromResult(new ReadResult(ReadOnlySequence<byte>.Empty, false, true));
        private static readonly ValueTask<ReadResult> _completeTask = ValueTask.FromResult(new ReadResult(ReadOnlySequence<byte>.Empty, false, true));
        
        public PipeReaderImpl(NexusDuplexPipe nexusPipe, PipeReader basePipe)
        {
            _nexusPipe = nexusPipe;
        }
        public override bool TryRead(out ReadResult result)
        {
            if (!CanRead())
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, false, true);
                return false;
            }

            using var lockToken = _readLock.TryWait(MutexSlim.WaitOptions.NoDelay);
            if (!lockToken.Success)
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isComplete);
                return false;
            }

            Buffer.Deallocate();

            return _basePipe.TryRead(out result);
        }

        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            if (!CanRead())
            {
                _nexusPipe._logger?.LogTrace("PipeReaderWrapper.ReadAsync can't read any data.");
                return _completeTask;
            }

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
