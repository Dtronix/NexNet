using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Logging;

namespace NexNet.Pipes;

internal class NexusPipeReader : PipeReader, IDisposable
{
    //private readonly NexusDuplexPipe _nexusDuplexPipe;
    private SemaphoreSlim? _readSemaphore = new SemaphoreSlim(0, 1);
    private readonly CancellationRegistrationArgs _cancelReadingArgs;

    private record CancellationRegistrationArgs(SemaphoreSlim Semaphore);

    private readonly BufferWriter<byte> _buffer = BufferWriter<byte>.Create();
    private readonly IPipeStateManager _stateManager;

    private bool _isCompleted;
    private bool _isCanceled;

    private readonly int _highWaterMark;
    private readonly int _highWaterCutoff;
    private readonly int _lowWaterMark;

    private readonly NexusDuplexPipe.State _backPressureFlag;
    private readonly NexusDuplexPipe.State _writingCompleteFlag;
    private readonly INexusLogger? _logger;
    private long _examinedPosition;
    private long _bufferTailPosition;

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

    /// <summary>
    /// Gets a value that indicates whether the pipe reader has been marked as complete.
    /// </summary>
    public bool IsCompleted => _isCompleted;

    public NexusPipeReader(
        IPipeStateManager stateManager,
        INexusLogger? logger,
        bool isServer, 
        int highWaterMark, 
        int highWaterCutoff,
        int lowWaterMark)
    {
        _stateManager = stateManager;
        _cancelReadingArgs = new CancellationRegistrationArgs(_readSemaphore);
        _logger = logger;
        _highWaterMark = highWaterMark; //_session!.Config.NexusPipeHighWaterMark;
        _highWaterCutoff = highWaterCutoff; //_session!.Config.NexusPipeHighWaterCutoff;
        _lowWaterMark = lowWaterMark; //_session!.Config.NexusPipeLowWaterMark;
        _backPressureFlag = !isServer
            ? NexusDuplexPipe.State.ServerWriterPause
            : NexusDuplexPipe.State.ClientWriterPause;

        _writingCompleteFlag = isServer
            ? NexusDuplexPipe.State.ClientWriterServerReaderComplete
            : NexusDuplexPipe.State.ClientReaderServerWriterComplete;

    }

    /// <summary>
    /// Buffers incoming data to the reader and notifies the reader that data is available.
    /// </summary>
    /// <param name="data">The incoming data as a ReadOnlySequence of bytes.</param>
    /// <returns>The length of the buffered data.</returns>
    public async ValueTask<NexusPipeBufferResult> BufferData(ReadOnlySequence<byte> data)
    {
        //using var lockToken = _readLock.TryWait(MutexSlim.WaitOptions.NoDelay);
        var length = (int)data.Length;
        var bufferLength = _buffer.Length + length;

        if (_highWaterCutoff != 0 && bufferLength >= _highWaterCutoff)
        {
            // Ensure the connection is not completed.
            if (_isCompleted)
                return NexusPipeBufferResult.DataIgnored;

            //_logger?.LogInfo($"Pipe {_stateManager.Id} has buffered {bufferLength} bytes of data and exceed the high water cutoff of {_highWaterCutoff}");
            //Todo: Review changing this out for a latch instead of a loop.
            int loopCount = 0;
            while (!_isCompleted)
            {
                //_logger?.LogInfo($"Pipe {_stateManager.Id} waiting for low water mark completion. Loop {i++}");
                // Do a short delay to allow the other side to process the data and progressively increase the delay.
                if (loopCount < 2)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }
                else if (loopCount < 10)
                {
                    await Task.Delay(5).ConfigureAwait(false);
                }
                else if (loopCount < 50)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                loopCount++;

                // Check to see if the back pressure flag was removed.  If so, then we can exit the waiting loop.
                if (_buffer.Length + length < _lowWaterMark)
                    break;
            }

            // Ensure the connection is not completed.
            if(_isCompleted)
                return NexusPipeBufferResult.DataIgnored;

            // If we have the back pressure flag, then we have not yet reached the low water mark.
            //if (_stateManager.CurrentState.HasFlag(_backPressureFlag))
            //{
            //    //return NexusPipeBufferResult.HighCutoffReached;
            //}
        }

        lock (_buffer)
        {
            _bufferTailPosition += length;

            // Get the updated length which may have changed while we were waiting.
            bufferLength = _buffer.Length + length;

            if (_isCompleted)
            {
                _logger?.LogTrace($"Already completed. Cancelling buffering.");
                return NexusPipeBufferResult.DataIgnored;
            }

            _logger?.LogTrace($"Buffered {length} new bytes.");

            // Copy the data to the buffer.
            data.CopyTo(_buffer.GetSpan(length));
            _buffer.Advance(length);
        }

        // If we have reached the high water mark, then notify the other side of the pipe.
        if (_highWaterMark != 0 && bufferLength >= _highWaterMark)
        {
            //_logger?.LogInfo($"Pipe {_stateManager.Id} has buffered {bufferLength} bytes of data and exceed the high water mark of {_highWaterMark}");

            if (_stateManager.UpdateState(_backPressureFlag))
                await _stateManager.NotifyState().ConfigureAwait(false);

            return NexusPipeBufferResult.HighWatermarkReached;
        }

        //Interlocked.Increment(ref _stateId);
        Utilities.TryReleaseSemaphore(_readSemaphore);

        return NexusPipeBufferResult.Success;
    }

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        _isCompleted = true;

        if (_stateManager.UpdateState(_writingCompleteFlag))
        {
            var semaphore = Interlocked.Exchange(ref _readSemaphore, null);
            Utilities.TryReleaseSemaphore(semaphore);
            return _stateManager.NotifyState();
        }

        return default;
    }

    public override void Complete(Exception? exception = null)
    {
        throw new NotImplementedException("Use CompleteAsync() instead.");
    }

    public void CompleteNoNotify()
    {
        _isCompleted = true;
        var semaphore = Interlocked.Exchange(ref _readSemaphore, null);
        Utilities.TryReleaseSemaphore(semaphore);
    }



    public override bool TryRead(out ReadResult result)
    {
        if (_isCompleted)
        {
            // There can still be data the buffer even if the pipe is completed.
            lock (_buffer)
            {
                result = new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
            }
            return true;
        }

        if (_isCanceled)
        {
            _isCanceled = false;
            // There can still be data the buffer even if the pipe is canceled.
            lock (_buffer)
            {
                result =  new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
            }
            return true;
        }
        
        lock (_buffer)
        {
            if (BufferedLength == 0)
            {
                result = new ReadResult(ReadOnlySequence<byte>.Empty, false, _isCompleted);
                return false;
            }

            result = new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
        }
        
        return true;
    }


    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        if (_isCompleted)
        {
            // There can still be data the buffer even if the pipe is completed.
            lock (_buffer)
            {
                return new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // There can still be data the buffer even if the pipe is canceled.
            lock (_buffer)
            {
                return new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
            }
        }

        if (_isCanceled)
        {
            _isCanceled = false;
            // There can still be data the buffer even if the pipe is canceled.
            lock (_buffer)
            {
                return new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
            }
        }

        CancellationTokenRegistration? cts = null;
        if (cancellationToken.CanBeCanceled)
        {
            cts = cancellationToken.UnsafeRegister(static argsObj =>
            {
                var args = Unsafe.As<CancellationRegistrationArgs>(argsObj)!;
                Utilities.TryReleaseSemaphore(args.Semaphore);

            }, _cancelReadingArgs);
        }

        if (_bufferTailPosition <= _examinedPosition && _isCanceled == false && _isCompleted == false)
        {
            try
            {
                // Check to see if we do in-fact have more data to read.  If we do, then bypass the wait.
                do
                {
                    if (_readSemaphore == null)
                        return new ReadResult(_buffer.GetBuffer(), false, _isCompleted);

                    await _readSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                } while (_bufferTailPosition <= _examinedPosition && _isCanceled == false && _isCompleted == false);
            }
            catch (OperationCanceledException)
            {
                // There can still be data the buffer even if the pipe is canceled.
                lock (_buffer)
                {
                    return new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
                }
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
            _readSemaphore?.Wait(0);

        }

        // Check again for common cases.
        if (_isCompleted)
        {
            // There can still be data the buffer even if the pipe is completed.
            lock (_buffer)
            {
                return new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
            }
        }

        if (_isCanceled)
        {
            _isCanceled = false;
            // There can still be data the buffer even if the pipe is canceled.
            lock (_buffer)
            {
                return new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
            }
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
        // ignore the back pressure if the completed flag is set.
        if (_isCompleted == false
            && _lowWaterMark != 0
            && _stateManager.CurrentState.HasFlag(_backPressureFlag) 
            && bufferLength <= _lowWaterMark)
        {
            // Remove the flag and notify the other side.
            if (_stateManager.UpdateState(_backPressureFlag, true))
            {
                await _stateManager.NotifyState().ConfigureAwait(false);

                //_logger?.LogInfo("Allow");
                // Set the task completion source to allow the next write to continue then assign a new one.
                //_allowBuffer.TrySetResult();
            }
        }

        return new ReadResult(readOnlySequence, cancellationToken.IsCancellationRequested, _isCompleted);
    }

    public override void AdvanceTo(SequencePosition consumed)
    {
        AdvanceTo(consumed, consumed);
    }

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        var examinedObject = examined.GetObject();
        if (examinedObject != null)
        {
            if (examinedObject is SequenceSegment<byte> examinedSegment)
            {
                _examinedPosition = examinedSegment.RunningIndex + examined.GetInteger();
            }
            else if (examined.GetInteger() == 0 && examinedObject.Equals(Array.Empty<byte>()))
            {
                // Provided an zero advance position. No need to update the examined position.
                return;
            }
            else
            {
                throw new InvalidOperationException($"Passed {nameof(examined)} argument is not a sequence from this pipe.");
            }
        }

        lock (_buffer)
        {
            _buffer.ReleaseTo(consumed);
        }
    }

    public void AdvanceTo(int count)
    {
        lock (_buffer)
        {
            _examinedPosition += count;
            _buffer.ReleaseTo(count);
        }
    }

    public override void CancelPendingRead()
    {
        _isCanceled = true;
        Utilities.TryReleaseSemaphore(_readSemaphore);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _readSemaphore, null)?.Dispose();

        lock (_buffer)
        {
            _buffer.Reset();
        }
    }
}
