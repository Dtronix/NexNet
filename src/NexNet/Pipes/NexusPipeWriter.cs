using System;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Internals;
using NexNet.Internals.Pipelines.Arenas;
using NexNet.Internals.Pipelines.Buffers;
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
    private readonly int _chunkSize;
    private readonly SemaphoreSlim _pauseSemaphore = new SemaphoreSlim(0, 1);
    internal bool _pauseWriting;
    private readonly INexusLogger? _logger;
    private readonly ISessionMessenger? _messenger;
    private readonly NexusDuplexPipe.State _completedFlag;
    private bool _hasPipeId;
    private int _flushCounter;

    /// <summary>
    /// Set to true to pause writing to the pipe.
    /// </summary>
    public bool PauseWriting
    {
        get => _pauseWriting;
        set
        {
            _pauseWriting = value;
            if (value == false)
            {
                Utilities.TryReleaseSemaphore(_pauseSemaphore);
            }
        }
    }

    public NexusPipeWriter(
        IPipeStateManager stateManager, 
        INexusLogger? logger,
        ISessionMessenger sessionMessenger, 
        bool isServer, int chunkSize)
    {
        _stateManager = stateManager;

        _messenger = sessionMessenger ?? throw new ArgumentNullException(nameof(sessionMessenger));
        _logger = logger;
        _chunkSize = chunkSize; // _session.Config.NexusPipeFlushChunkSize;

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
    }

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
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

        // If we are paused, wait for the semaphore to be released.
        if (PauseWriting)
        {
            try
            {
                await _pauseSemaphore.WaitAsync(_flushCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ensure the cancellation token is canceled null so it will be created again.
                _flushCts.Dispose();
                _flushCts = null;
                return new FlushResult(true, _isCompleted);
            }
            finally
            {
                if(cancellationTokenRegistration != null)
                    await cancellationTokenRegistration.Value.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (!_hasPipeId)
        {
            BitConverter.TryWriteBytes(_pipeId.Span, _stateManager.Id);
            _hasPipeId = true;
        }

        var buffer = _bufferWriter.GetBuffer();

        var multiPartSend = bufferLength > _chunkSize;

        var sendingBuffer = multiPartSend
            ? buffer.Slice(0, _chunkSize)
            : buffer;

        var flushPosition = 0;

        if (_messenger == null)
        {
            if (cancellationTokenRegistration != null)
                await cancellationTokenRegistration.Value.DisposeAsync().ConfigureAwait(false);

            throw new InvalidOperationException("Session is null.");
        }

        while (true)
        {
            if (_isCanceled)
                break;

            try
            {
                // TODO: Remove!
                var flushId = Interlocked.Increment(ref _flushCounter);
                _logger?.LogTrace($"Sending[id:{flushId}] {sendingBuffer.Length} bytes [{string.Join(",", sendingBuffer.ToArray())}]");
                // We are passing the cancellation token from the method instead of the _flushCts due to
                // the fact that the _flushCts can be canceled even after this method is completed due
                // to some transport implementations such as QUIC.
                await _messenger.SendHeaderWithBody(
                    MessageType.DuplexPipeWrite,
                    _pipeId,
                    sendingBuffer,
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
            if (_stateManager.UpdateState(_completedFlag))
                await _stateManager.NotifyState().ConfigureAwait(false);
        }

        if(cancellationTokenRegistration != null)
            await cancellationTokenRegistration.Value.DisposeAsync().ConfigureAwait(false);

        return new FlushResult(_isCanceled, _isCompleted);
    }

    public void Dispose()
    {
        _bufferWriter.Dispose();
        _flushCts?.Dispose();
        _pauseSemaphore.Dispose();
    }
}
