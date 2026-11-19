using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Diagnostics;
using NexNet.Internals;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Internals.Threading;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Pipes;

/// <summary>
/// Sends pipe data frames on behalf of a <see cref="NexusPipeWriter"/>.
/// </summary>
internal interface IPipeChunkSender
{
    /// <summary>
    /// Sends one DuplexPipeWrite frame, unless <paramref name="writer"/> has completed by the time the frame would
    /// be written: the pipe's Complete is sent after the writer completes, and no data may follow it.
    /// </summary>
    ValueTask SendPipeChunk(
        NexusPipeWriter writer,
        ReadOnlyMemory<byte> pipeId,
        ReadOnlySequence<byte> body,
        bool flush,
        CancellationToken cancellationToken);
}

internal class NexusPipeWriter : PipeWriter, IDisposable
{
    private readonly IPipeStateManager _stateManager;

    private readonly BufferWriter _bufferWriter = BufferWriter.Create(1024 * 64);
    private bool _isCanceled;
    // Read by the session under its write lock while another thread completes the writer.
    private volatile bool _isCompleted;
    private CancellationTokenSource? _flushCts;
    private readonly Memory<byte> _pipeId = new byte[sizeof(ushort)];
    /// <summary>
    /// Largest pipe write sent in one frame: the maximum frame body. The pipe ID travels in the frame header.
    /// </summary>
    internal const int MaxChunkSize = ushort.MaxValue;

    private readonly int _chunkSize;
    private readonly SemaphoreSlim _flushSemaphore = new SemaphoreSlim(1, 1);

    // Send credit granted by the peer (docs/internals/flow-control.md, section 4.4). A flush sends what the
    // credit allows and waits, in the writer's task, for more.
    private long _credit;
    private readonly SemaphoreSlim _creditSignal = new SemaphoreSlim(0, 1);

    /// <summary>
    /// Most credit the peer may grant on a pipe; more is a protocol violation.
    /// </summary>
    internal const long MaxCredit = 1L << 31;
    private readonly INexusLogger? _logger;
    private readonly ISessionMessenger? _messenger;
    private readonly NexusDuplexPipe.State _completedFlag;
    private bool _hasPipeId;
    private int _flushCounter;

    // Dispose can run on another thread while the application writes or flushes (the pipe is released or reset
    // mid-write). Buffers are returned to the pool only when nothing references them: if a flush is sending from
    // them, or the application holds a span or memory from GetSpan/GetMemory, the writer's own thread releases them
    // when it is done (end of the flush, Advance, the next flush or CompleteAsync). After Dispose, written data is
    // discarded.
    private readonly object _disposeLock = new();
    private bool _flushInProgress;
    private volatile bool _disposed;
    private bool _released;
    private int _bufferInUse;
    private byte[]? _discard;

    // While a flush waits for credit: when the wait started (TimeProvider tick count, 0 when not waiting) and the
    // bytes left to send. Written on the wait path only, read by snapshots.
    private readonly TimeProvider _time;
    private long _creditWaitSince;
    private long _waitingBytes;
    private readonly MetricsTagSet? _metricsTags;

    /// <summary>
    /// Tick count when the current wait for credit started, or 0; and the bytes left to send.
    /// </summary>
    public (long WaitSince, long WaitingBytes) CreditWait
    {
        get
        {
            var since = Volatile.Read(ref _creditWaitSince);
            return (since, since == 0 ? 0 : Volatile.Read(ref _waitingBytes));
        }
    }

    /// <summary>
    /// True once the writer completed.
    /// </summary>
    public bool IsCompleted => _isCompleted;

    /// <summary>
    /// Send credit currently available.
    /// </summary>
    public long Credit => Volatile.Read(ref _credit);

    /// <summary>
    /// Adds credit granted by the peer. Returns false if the total would exceed <see cref="MaxCredit"/>.
    /// </summary>
    public bool AddCredit(long bytes)
    {
        if (Interlocked.Add(ref _credit, bytes) > MaxCredit)
            return false;

        _creditSignal.TryReleaseSemaphore();
        return true;
    }

    /// <summary>
    /// Waits until send credit is available and returns it, or returns 0 once the writer completed. Lets a caller
    /// write no more than the credit, so a flush that follows sends everything without waiting. The wait shows in
    /// <see cref="CreditWait"/> like a flush's.
    /// </summary>
    /// <param name="bytes">Bytes the caller wants to send, reported while waiting.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    internal async ValueTask<long> WaitForCreditAsync(long bytes, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (_isCompleted)
                    return 0;

                var credit = Volatile.Read(ref _credit);
                if (credit > 0)
                    return credit;

                if (_creditWaitSince == 0)
                {
                    Volatile.Write(ref _waitingBytes, bytes);
                    Volatile.Write(ref _creditWaitSince, Math.Max(1, _time.GetTickCount64()));
                }

                await _creditSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_creditWaitSince != 0)
                EndCreditWait();
        }
    }

    public NexusPipeWriter(
        IPipeStateManager stateManager,
        INexusLogger? logger,
        ISessionMessenger sessionMessenger, 
        bool isServer, int chunkSize,
        TimeProvider? time = null,
        MetricsTagSet? metricsTags = null)
    {
        _stateManager = stateManager;
        _time = time ?? TimeProvider.System;
        _metricsTags = metricsTags;

        _messenger = sessionMessenger ?? throw new ArgumentNullException(nameof(sessionMessenger));
        _logger = logger;
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxChunkSize);
        _chunkSize = chunkSize;

        _completedFlag = isServer
            ? NexusDuplexPipe.State.ClientReaderServerWriterComplete
            : NexusDuplexPipe.State.ClientWriterServerReaderComplete;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Advance(int bytes)
    {
        if (_disposed)
        {
            // The pipe is closed: the bytes are dropped.
            AdvanceDisposed(bytes);
            return;
        }

        _bufferWriter.Advance(bytes);
        Volatile.Write(ref _bufferInUse, 0);

        // Dispose ran while the application held the span; it left the buffers to this thread.
        if (_disposed)
            ReleaseDeferred();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        // Marked before Dispose is checked, and Dispose marks itself before it checks this: one of them sees the other.
        Interlocked.Exchange(ref _bufferInUse, 1);
        return _disposed ? GetDiscard(sizeHint) : _bufferWriter.GetMemory(sizeHint);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        Interlocked.Exchange(ref _bufferInUse, 1);
        return _disposed ? GetDiscard(sizeHint).Span : _bufferWriter.GetSpan(sizeHint);
    }

    private void AdvanceDisposed(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Volatile.Write(ref _bufferInUse, 0);
        ReleaseDeferred();
    }

    /// <summary>
    /// Scratch memory for writes after Dispose, whose data is dropped.
    /// </summary>
    private Memory<byte> GetDiscard(int sizeHint)
    {
        ReleaseDeferred();
        var size = Math.Max(sizeHint, 4096);
        if (_discard == null || _discard.Length < size)
            _discard = new byte[size];
        return _discard;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void CancelPendingFlush()
    {
        _isCanceled = true;
        try
        {
            _flushCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // noop
        }
    }

    public override ValueTask CompleteAsync(Exception? exception = null)
    {
        _isCompleted = true;

        if (_disposed)
        {
            // The application is done writing; buffers Dispose left behind can go back to the pool.
            Volatile.Write(ref _bufferInUse, 0);
            ReleaseDeferred();
        }

        if (_messenger == null)
            return default;

        if (_stateManager.UpdateState(_completedFlag))
            return _stateManager.NotifyState();

        return default;
    }

    public override void Complete(Exception? exception = null)
    {
        throw new InvalidOperationException("Use CompleteAsync instead.");
    }


    public void SetComplete()
    {
        _isCompleted = true;

        // Wake a flush waiting for credit.
        _creditSignal.TryReleaseSemaphore();
    }

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        // Ensure no concurrent flushing occurs.
        using var _ = await _flushSemaphore.WaitDisposableAsync().ConfigureAwait(false);

        // A flush ends the application's use of any span or memory it got.
        Volatile.Write(ref _bufferInUse, 0);

        lock (_disposeLock)
        {
            _flushInProgress = !_disposed;
        }

        if (!_flushInProgress)
        {
            // Closed: nothing written is sent.
            ReleaseDeferred();
            return new FlushResult(_isCanceled, true);
        }

        try
        {
            return await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_disposeLock)
            {
                _flushInProgress = false;
            }

            // Dispose ran during this flush and left the buffers to it.
            if (_disposed)
                ReleaseDeferred();
        }
    }

    private async ValueTask<FlushResult> FlushCoreAsync(CancellationToken cancellationToken)
    {
        if (_isCompleted)
            return new FlushResult(_isCanceled, _isCompleted);

        if (_flushCts?.IsCancellationRequested == true)
            return new FlushResult(_isCanceled, _isCompleted);

        _flushCts ??= new CancellationTokenSource();

        var bufferLength = _bufferWriter.Length;

        // Shortcut for empty buffer.
        if (bufferLength == 0)
            return new FlushResult(_isCanceled, _isCompleted);

        // ReSharper disable once UseAwaitUsing
        // TODO: Review only calling when the token can be canceled.

        CancellationTokenRegistration? cancellationTokenRegistration = null;

        // Only register the cancellation token if it can be canceled.
        if (cancellationToken.CanBeCanceled)
        {
            cancellationTokenRegistration = cancellationToken.Register(static (object? ctsObject) =>
            {
                Unsafe.As<CancellationTokenSource?>(ctsObject)?.Cancel();
            }, _flushCts);
        }

        if (!_hasPipeId)
        {
            // Pipe ID frame bytes: [client id][server id].
            var pipeIdSpan = _pipeId.Span;
            var pipeId = _stateManager.Id;
            pipeIdSpan[0] = (byte)pipeId;
            pipeIdSpan[1] = (byte)(pipeId >> 8);
            _hasPipeId = true;
        }

        var buffer = _bufferWriter.GetBuffer();
        long flushPosition = 0;

        // Cancels this flush only; unlike CancelPendingFlush it does not cancel later ones.
        var canceledWhileWaiting = false;

        if (_messenger == null)
        {
            if (cancellationTokenRegistration != null)
                await cancellationTokenRegistration.Value.DisposeAsync().ConfigureAwait(false);

            throw new InvalidOperationException("Session is null.");
        }

        while (bufferLength > 0)
        {
            if (_isCanceled || _isCompleted)
                break;

            // Wait for credit. This is the writer's own task; the session's read loop never waits here.
            var credit = Volatile.Read(ref _credit);
            if (credit <= 0)
            {
                if (_creditWaitSince == 0)
                {
                    Volatile.Write(ref _waitingBytes, bufferLength);
                    Volatile.Write(ref _creditWaitSince, Math.Max(1, _time.GetTickCount64()));
                }

                try
                {
                    await _creditSignal.WaitAsync(_flushCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    canceledWhileWaiting = true;
                    break;
                }

                continue;
            }

            if (_creditWaitSince != 0)
                EndCreditWait();

            var chunkLength = (int)Math.Min(Math.Min(bufferLength, credit), _chunkSize);
            Interlocked.Add(ref _credit, -chunkLength);
            var sendingBuffer = buffer.Slice(flushPosition, chunkLength);

            // Flush the transport once for everything the current credit allows, not once per chunk.
            var flush = chunkLength == bufferLength || chunkLength == credit;

            try
            {
                Interlocked.Increment(ref _flushCounter);
                // We are passing the cancellation token from the method instead of the _flushCts due to
                // the fact that the _flushCts can be canceled even after this method is completed due
                // to some transport implementations such as QUIC.
                if (_messenger is IPipeChunkSender chunkSender)
                {
                    // Dropped if this writer completes before the frame is written (see IPipeChunkSender).
                    await chunkSender.SendPipeChunk(this, _pipeId, sendingBuffer, flush, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _messenger.SendHeaderWithBody(
                        MessageType.DuplexPipeWrite,
                        _pipeId,
                        sendingBuffer,
                        flush,
                        cancellationToken).ConfigureAwait(false);
                }
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
            catch (Exception e) when (_isCompleted)
            {
                // The pipe closed while this chunk was being sent; only this pipe ends, never the session.
                _logger?.LogDebug(e, $"Pipe closed while writing on Invocation Id: {_stateManager.Id}.");
                break;
            }
            catch (Exception e)
            {
                _logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_stateManager.Id}.");
                await _messenger.DisconnectAsync(DisconnectReason.ProtocolError).ConfigureAwait(false);
                break;
            }

            bufferLength -= chunkLength;
            flushPosition += chunkLength;
        }

        _bufferWriter.Deallocate(buffer);
        if (_creditWaitSince != 0)
            EndCreditWait();

        // Try to reset the CTS.  If we can't just set it to null so a new one will be instanced.
        if (!_flushCts.TryReset())
            _flushCts = null;

        if (_isCompleted)
        {
            if (_stateManager.UpdateState(_completedFlag))
                await _stateManager.NotifyState().ConfigureAwait(false);
        }

        if(cancellationTokenRegistration != null)
            await cancellationTokenRegistration.Value.DisposeAsync().ConfigureAwait(false);

        return new FlushResult(_isCanceled || canceledWhileWaiting, _isCompleted);
    }

    /// <summary>
    /// Ends the current wait for credit and records how long it lasted.
    /// </summary>
    private void EndCreditWait()
    {
        var waitMs = Math.Max(0, _time.GetTickCount64() - _creditWaitSince);
        Volatile.Write(ref _creditWaitSince, 0);
        _metricsTags?.Metrics.RecordPipeCreditWait(_metricsTags, waitMs / 1000.0);
    }

    /// <summary>
    /// Completes the writer and releases its buffers. Safe to call while the application writes or flushes on
    /// another thread: a flush in progress stops after the chunk being sent, and buffers still in use are released by
    /// the writer's own thread once it is done with them.
    /// </summary>
    public void Dispose()
    {
        // Stops a flush in progress after its current chunk, and wakes one waiting for credit.
        SetComplete();

        lock (_disposeLock)
        {
            if (_disposed)
                return;

            _disposed = true;

            // Full fence: pairs with the exchange in GetSpan/GetMemory.
            Interlocked.MemoryBarrier();
            if (_flushInProgress || Volatile.Read(ref _bufferInUse) != 0)
                return;

            _released = true;
        }

        ReleaseResources();
    }

    /// <summary>
    /// Releases the buffers once Dispose ran, unless a flush still sends from them or they are already released.
    /// Called by the writer's own thread when it stops using them.
    /// </summary>
    private void ReleaseDeferred()
    {
        lock (_disposeLock)
        {
            if (!_disposed || _released || _flushInProgress)
                return;

            _released = true;
        }

        ReleaseResources();
    }

    private void ReleaseResources()
    {
        _bufferWriter.Dispose();
        _flushCts?.Dispose();

        // The semaphores are not disposed: a flush may still be waiting on or releasing them, and SemaphoreSlim
        // holds nothing that needs disposing unless its wait handle is used.
    }
}
