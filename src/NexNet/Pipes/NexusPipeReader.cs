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
    private readonly Lock _bufferLock = new Lock();
    private readonly IPipeStateManager _stateManager;

    private bool _isCompleted;
    private bool _isCanceled;

    // Receive window (docs/internals/flow-control-v2.md, section 4.4). _credit is what the peer may still send;
    // consumed bytes are granted back once they reach half the window. Guarded by _bufferLock.
    private readonly int _window;
    private long _credit;
    private long _consumedSinceGrant;

    private readonly NexusDuplexPipe.State _writingCompleteFlag;
    private readonly INexusLogger? _logger;
    private readonly TimeProvider _time;
    private long _examinedPosition;
    private long _bufferTailPosition;

    /// <summary>
    /// Length of data that has been buffered.
    /// </summary>
    public long BufferedLength
    {
        get
        {
            lock (_bufferLock)
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
        int window,
        TimeProvider? time = null)
    {
        _stateManager = stateManager;
        _cancelReadingArgs = new CancellationRegistrationArgs(_readSemaphore);
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _window = window;

        _writingCompleteFlag = isServer
            ? NexusDuplexPipe.State.ClientWriterServerReaderComplete
            : NexusDuplexPipe.State.ClientReaderServerWriterComplete;

    }

    /// <summary>
    /// The receive window: bytes the peer may have in flight or buffered here.
    /// </summary>
    public int Window => _window;

    /// <summary>
    /// Opens the window: the peer may now send <see cref="Window"/> bytes. Called when the window is granted.
    /// </summary>
    public void OpenWindow()
    {
        lock (_bufferLock)
            _credit = _window;
    }

    /// <summary>
    /// Buffers incoming data to the reader and notifies the reader that data is available. Never waits: the peer
    /// only sends what it has credit for, and the space for that is reserved.
    /// </summary>
    /// <param name="data">The incoming data as a ReadOnlySequence of bytes.</param>
    /// <returns>The result of the buffering; <see cref="NexusPipeBufferResult.CreditExceeded"/> is a protocol
    /// violation by the peer.</returns>
    public NexusPipeBufferResult BufferData(ReadOnlySequence<byte> data)
    {
        var length = (int)data.Length;

        lock (_bufferLock)
        {
            if (length > _credit)
                return NexusPipeBufferResult.CreditExceeded;

            // Discarded data still used credit; it is not granted back, as the peer stops sending once it learns
            // this side completed.
            _credit -= length;

            if (_isCompleted)
            {
                _logger?.LogTrace($"Already completed. Discarded {length} bytes.");
                return NexusPipeBufferResult.DataIgnored;
            }

            _bufferTailPosition += length;
            data.CopyTo(_buffer.GetSpan(length));
            _buffer.Advance(length);
        }

        Internals.Threading.Utilities.TryReleaseSemaphore(_readSemaphore);
        return NexusPipeBufferResult.Success;
    }

    /// <summary>
    /// Records <paramref name="released"/> consumed bytes and grants credit back to the peer once at least half the
    /// window has been consumed since the last grant. Called with <see cref="_bufferLock"/> held.
    /// </summary>
    private void OnConsumed(long released)
    {
        if (released <= 0 || _isCompleted)
            return;

        _consumedSinceGrant += released;
        if (_consumedSinceGrant < _window / 2)
            return;

        var grant = (int)_consumedSinceGrant;
        _consumedSinceGrant = 0;
        _credit += grant;
        _stateManager.GrantCredit(grant);
    }

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        _isCompleted = true;

        if (_stateManager.UpdateState(_writingCompleteFlag))
        {
            var semaphore = Interlocked.Exchange(ref _readSemaphore, null);
            Internals.Threading.Utilities.TryReleaseSemaphore(semaphore);
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
        Internals.Threading.Utilities.TryReleaseSemaphore(semaphore);
    }



    public override bool TryRead(out ReadResult result)
    {
        if (_isCompleted)
        {
            // There can still be data the buffer even if the pipe is completed.
            lock (_bufferLock)
            {
                result = new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
            }
            return true;
        }

        if (_isCanceled)
        {
            _isCanceled = false;
            // There can still be data the buffer even if the pipe is canceled.
            lock (_bufferLock)
            {
                result =  new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
            }
            return true;
        }
        
        lock (_bufferLock)
        {
            if (_bufferTailPosition - _examinedPosition == 0) // BufferedLength
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
            lock (_bufferLock)
            {
                return new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // There can still be data the buffer even if the pipe is canceled.
            lock (_bufferLock)
            {
                return new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
            }
        }

        if (_isCanceled)
        {
            _isCanceled = false;
            // There can still be data the buffer even if the pipe is canceled.
            lock (_bufferLock)
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
                Internals.Threading.Utilities.TryReleaseSemaphore(args.Semaphore);

            }, _cancelReadingArgs);
        }

        if (_bufferTailPosition <= _examinedPosition && _isCanceled == false && _isCompleted == false)
        {
            try
            {
                // Check to see if we do in-fact have more data to read.  If we do, then bypass the wait.
                do
                {
                    // Hot path for if the semaphore has already been disposed.
                    if (_readSemaphore == null)
                    {
                        lock (_bufferLock)
                            return new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
                    }
                    
                    try
                    {
                        await _readSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (NullReferenceException e)
                    {
                        // Slow path for if the semaphore has already been swapped out with null between the check above and now.
                        _logger?.LogInfo(e, "Semaphore is null and can not be acquired.");
                        lock (_bufferLock)
                            return new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
                    }

                } while (_bufferTailPosition <= _examinedPosition && _isCanceled == false && _isCompleted == false);
            }
            catch (OperationCanceledException)
            {
                // There can still be data the buffer even if the pipe is canceled.
                lock (_bufferLock)
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
            lock (_bufferLock)
            {
                return new ReadResult(_buffer.GetBuffer(), false, _isCompleted);
            }
        }

        if (_isCanceled)
        {
            _isCanceled = false;
            // There can still be data the buffer even if the pipe is canceled.
            lock (_bufferLock)
            {
                return new ReadResult(_buffer.GetBuffer(), true, _isCompleted);
            }
        }

        ReadOnlySequence<byte> readOnlySequence;
        lock (_bufferLock)
            readOnlySequence = _buffer.GetBuffer();

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
                // Provided a zero advance position. No need to update the examined position.
                return;
            }
            else
            {
                throw new InvalidOperationException($"Passed {nameof(examined)} argument is not a sequence from this pipe.");
            }
        }

        lock (_bufferLock)
        {
            var before = _buffer.Length;
            _buffer.ReleaseTo(consumed);
            OnConsumed(before - _buffer.Length);
        }
    }

    public void AdvanceTo(int consumed, int examined)
    {
        if(consumed > examined)
            throw new ArgumentOutOfRangeException(nameof(consumed), "Consumed amount must be greater than or equal to the examined amount.");
        lock (_bufferLock)
        {
            _examinedPosition += examined;

            if (consumed > 0)
            {
                _buffer.ReleaseTo(consumed);
                OnConsumed(consumed);
            }
        }
    }

    /// <summary>
    /// Consumes <paramref name="consumed"/> bytes from the start of the current buffer and marks
    /// <paramref name="examined"/> bytes (measured from the start of the current buffer) as examined.
    /// Marking all buffered bytes as examined makes the next read wait for new data.
    /// </summary>
    internal void AdvanceToExamined(long consumed, long examined)
    {
        if (consumed > examined)
            throw new ArgumentOutOfRangeException(nameof(consumed), "Consumed amount must be less than or equal to the examined amount.");

        lock (_bufferLock)
        {
            var bufferStart = _bufferTailPosition - _buffer.Length;
            var examinedAbsolute = bufferStart + examined;
            if (examinedAbsolute > _examinedPosition)
                _examinedPosition = examinedAbsolute;

            if (consumed > 0)
            {
                _buffer.ReleaseTo((int)consumed);
                OnConsumed(consumed);
            }
        }
    }

    public override void CancelPendingRead()
    {
        _isCanceled = true;
        Internals.Threading.Utilities.TryReleaseSemaphore(_readSemaphore);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _readSemaphore, null)?.Dispose();

        lock (_bufferLock)
        {
            _buffer.Reset();
        }
    }
}
