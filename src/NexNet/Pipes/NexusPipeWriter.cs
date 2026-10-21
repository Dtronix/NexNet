using System;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
using NexNet.Internals.Threading;
using NexNet.Logging;
using NexNet.Messages;

namespace NexNet.Pipes;

internal class NexusPipeWriter : PipeWriter, IDisposable
{
    private readonly IPipeStateManager _stateManager;

    private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
    private bool _isCanceled;
    private bool _isCompleted;
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

    // While a flush waits for credit: when the wait started (TimeProvider tick count, 0 when not waiting) and the
    // bytes left to send. Written on the wait path only, read by snapshots.
    private readonly TimeProvider _time;
    private long _creditWaitSince;
    private long _waitingBytes;

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

    public NexusPipeWriter(
        IPipeStateManager stateManager, 
        INexusLogger? logger,
        ISessionMessenger sessionMessenger, 
        bool isServer, int chunkSize,
        TimeProvider? time = null)
    {
        _stateManager = stateManager;
        _time = time ?? TimeProvider.System;

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
                Volatile.Write(ref _creditWaitSince, 0);

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
                await _messenger.SendHeaderWithBody(
                    MessageType.DuplexPipeWrite,
                    _pipeId,
                    sendingBuffer,
                    flush,
                    cancellationToken).ConfigureAwait(false);
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
                _logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_stateManager.Id}.");
                await _messenger.DisconnectAsync(DisconnectReason.ProtocolError).ConfigureAwait(false);
                break;
            }

            bufferLength -= chunkLength;
            flushPosition += chunkLength;
        }

        _bufferWriter.Deallocate(buffer);
        if (_creditWaitSince != 0)
            Volatile.Write(ref _creditWaitSince, 0);

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

    public void Dispose()
    {
        _bufferWriter.Dispose();
        _flushCts?.Dispose();
        _creditSignal.Dispose();
        _flushSemaphore.Dispose();
    }
}
