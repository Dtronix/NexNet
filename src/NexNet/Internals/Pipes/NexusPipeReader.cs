using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.Internals.Pipes;

internal class NexusPipeReader : PipeReader
{
    //private readonly NexusDuplexPipe _nexusDuplexPipe;
    private readonly SemaphoreSlim _readSemaphore = new SemaphoreSlim(0, 1);
    private readonly CancellationRegistrationArgs _cancelReadingArgs;

    private record CancellationRegistrationArgs(SemaphoreSlim Semaphore);

    private readonly BufferWriter<byte> _buffer = BufferWriter<byte>.Create();
    private readonly IPipeStateManager _nexusDuplexPipe;

    private bool _isCompleted;
    private bool _isCanceled;

    private int _highWaterCutoff;
    private int _lowWaterMark;

    private NexusDuplexPipe.State _backPressureFlag;
    private NexusDuplexPipe.State _writingCompleteFlag;

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

    public NexusPipeReader(IPipeStateManager stateManager)
    {
        _nexusDuplexPipe = stateManager;
        _cancelReadingArgs = new CancellationRegistrationArgs(_readSemaphore);
    }

    public void Setup(bool isServer, int highWaterCutoff, int lowWaterMark)
    {
        _highWaterCutoff = highWaterCutoff; //_session!.Config.NexusPipeHighWaterCutoff;
        _lowWaterMark = lowWaterMark; //_session!.Config.NexusPipeLowWaterMark;
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

        if (_nexusDuplexPipe.UpdateState(_writingCompleteFlag))
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


        // If we currently have back pressure, and the buffer length is below the low water mark, then we need to
        // notify the other side that we are ready to receive more data.
        if (_nexusDuplexPipe.CurrentState.HasFlag(_backPressureFlag) && bufferLength <= _lowWaterMark)
        {
            // Remove the flag and notify the other side.
            if (_nexusDuplexPipe.UpdateState(_backPressureFlag, true))
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
