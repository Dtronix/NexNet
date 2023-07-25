﻿using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial.Arenas;
using Pipelines.Sockets.Unofficial.Buffers;
using Pipelines.Sockets.Unofficial.Internal;

namespace NexNet.Internals.Pipes;

internal class NexusPipeReader : PipeReader
{
    //private readonly NexusDuplexPipe _nexusDuplexPipe;
    private readonly SemaphoreSlim _readSemaphore = new SemaphoreSlim(0, 1);
    private readonly CancellationRegistrationArgs _cancelReadingArgs;

    private record CancellationRegistrationArgs(SemaphoreSlim Semaphore);

    private readonly BufferWriter<byte> _buffer = BufferWriter<byte>.Create();
    private readonly IPipeStateManager _stateManager;

    private bool _isCompleted;
    private bool _isCanceled;

    private int _highWaterMark;
    private int _highWaterCutoff;
    private int _lowWaterMark;

    private NexusDuplexPipe.State _backPressureFlag;
    private NexusDuplexPipe.State _writingCompleteFlag;
    private INexusLogger? _logger;
    private long _examinedPosition;
    private long _bufferTailPosition;
    private bool _waiting;


    /// <summary>
    /// Length of data that has been buffered.
    /// </summary>
    public long BufferedLength
    {
        get
        {
            lock (_buffer)
                return _bufferTailPosition - _examinedPosition;
        }
    }

    public NexusPipeReader(IPipeStateManager stateManager)
    {
        _stateManager = stateManager;
        _cancelReadingArgs = new CancellationRegistrationArgs(_readSemaphore);
    }

    public void Setup(INexusLogger? logger, bool isServer, int highWaterMark, int highWaterCutoff, int lowWaterMark)
    {
        _logger = logger;
        _highWaterMark = highWaterMark; //_session!.Config.NexusPipeHighWaterMark;
        _highWaterCutoff = highWaterCutoff; //_session!.Config.NexusPipeHighWaterCutoff;
        _lowWaterMark = lowWaterMark; //_session!.Config.NexusPipeLowWaterMark;
        _examinedPosition = 0;
        _bufferTailPosition = 0;
        _backPressureFlag = isServer
            ? NexusDuplexPipe.State.ServerReaderBackPressure
            : NexusDuplexPipe.State.ClientReaderBackPressure;

        _writingCompleteFlag = isServer
            ? NexusDuplexPipe.State.ClientWriterServerReaderComplete
            : NexusDuplexPipe.State.ClientReaderServerWriterComplete;
    }

    /// <summary>
    /// Resets the reader to it's initial state for reuse.
    /// </summary>
    public void Reset()
    {
        _isCompleted = false;
        _isCanceled = false;
        _logger = null;

        lock (_buffer)
        {
            _buffer.Dispose();
        }

        // Reset the semaphore to it's original state.
        _readSemaphore.Wait(0);
    }

    /// <summary>
    /// Buffers incoming data to the reader and notifies the reader that data is available.
    /// </summary>
    /// <param name="data">The incoming data as a ReadOnlySequence of bytes.</param>
    /// <returns>The length of the buffered data.</returns>
    public async ValueTask<NexusPipeBufferResult> BufferData(ReadOnlySequence<byte> data)
    {
        long bufferLength;
        //using var lockToken = _readLock.TryWait(MutexSlim.WaitOptions.NoDelay);
        lock (_buffer)
        {
            var length = (int)data.Length;
            bufferLength = _buffer.Length + length;
            _bufferTailPosition += length;
            if (bufferLength >= _highWaterCutoff)
            {
                _logger?.LogInfo(
                    $"Pipe {_stateManager.Id} has buffered {bufferLength} bytes of data and exceed the high water cutoff of {_highWaterCutoff}");

                return NexusPipeBufferResult.HighCutoffReached;
            }

            // Copy the data to the buffer.
            data.CopyTo(_buffer.GetSpan(length));
            _buffer.Advance(length);
        }

        //Interlocked.Increment(ref _stateId);
        TryReleaseSemaphore(_readSemaphore);


        // If we have reached the high water mark, then notify the other side of the pipe.
        if (bufferLength >= _highWaterMark)
        {
            _logger?.LogTrace(
                $"Pipe {_stateManager.Id} has buffered {bufferLength} bytes of data and exceed the high water mark of {_highWaterMark}");

            if (_stateManager.UpdateState(_backPressureFlag))
                await _stateManager.NotifyState();

            return NexusPipeBufferResult.HighWatermarkReached;
        }
        
        return NexusPipeBufferResult.Success;
    }

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        _isCompleted = true;

        if (_stateManager.UpdateState(_writingCompleteFlag))
            return _stateManager.NotifyState();

        return default;
    }


    public override bool TryRead(out ReadResult result)
    {
        
    }


    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        if (_isCompleted)
            return new ReadResult(ReadOnlySequence<byte>.Empty, false, _isCompleted);

        if (cancellationToken.IsCancellationRequested)
            return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);

        if (_isCanceled)
        {
            _isCanceled = false;
            var result = new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
            return result;
        }

        CancellationTokenRegistration? cts = null;
        if (cancellationToken.CanBeCanceled)
        {
            cts = cancellationToken.UnsafeRegister(static argsObj =>
            {
                var args = Unsafe.As<CancellationRegistrationArgs>(argsObj)!;
                TryReleaseSemaphore(args.Semaphore);

            }, _cancelReadingArgs);
        }

        if (_bufferTailPosition <= _examinedPosition && _isCanceled == false && _isCompleted == false)
        {
            try
            {
                // Check to see if we do in-fact have more data to read.  If we do, then bypass the wait.
                do
                {
                    _waiting = true;
                    await _readSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    _waiting = false;
                } while (_bufferTailPosition <= _examinedPosition && _isCanceled == false && _isCompleted == false);
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
        }
        else
        {
            // ReSharper disable once MethodHasAsyncOverloadWithCancellation
            _readSemaphore.Wait(0);

        }

        // Check again for common cases.
        if (_isCompleted)
            return new ReadResult(ReadOnlySequence<byte>.Empty, _isCanceled, _isCompleted);

        if (_isCanceled)
        {
            _isCanceled = false;
            return new ReadResult(ReadOnlySequence<byte>.Empty, true, _isCompleted);
        }

        ReadOnlySequence<byte> readOnlySequence;
        long bufferLength;
        lock (_buffer)
        {
            readOnlySequence = _buffer.GetBuffer();
            bufferLength = _buffer.Length;
        }

        // If we currently have back pressure, and the buffer length is below the low water mark, then we need to
        // notify the other side that we are ready to receive more data.
        if (_stateManager.CurrentState.HasFlag(_backPressureFlag) && bufferLength <= _lowWaterMark)
        {
            // Remove the flag and notify the other side.
            if (_stateManager.UpdateState(_backPressureFlag, true))
                await _stateManager.NotifyState().ConfigureAwait(false);
        }

        return new ReadResult(readOnlySequence, cancellationToken.IsCancellationRequested, _isCompleted);
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        AdvanceTo(consumed, consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (examined.GetObject() is not SequenceSegment<byte> examinedSegment)
            throw new InvalidOperationException($"Passed {nameof(examined)} argument is not a sequence from this pipe.");

        lock (_buffer)
        {
            _examinedPosition = examinedSegment.RunningIndex + examined.GetInteger();
            _buffer.ReleaseTo(consumed);
        }
    }

    public override void CancelPendingRead()
    {
        _isCanceled = true;
        TryReleaseSemaphore(_readSemaphore);
    }

    public override void Complete(Exception? exception = null)
    {
        _isCompleted = true;
        TryReleaseSemaphore(_readSemaphore);
    }

    /// <summary>
    /// Releases the semaphore if it is currently held.
    /// </summary>
    /// <param name="semaphore"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryReleaseSemaphore(SemaphoreSlim semaphore)
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
