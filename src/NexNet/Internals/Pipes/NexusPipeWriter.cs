﻿using System;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NexNet.Messages;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.Internals.Pipes;

internal class NexusPipeWriter : PipeWriter
{
    private readonly IPipeStateManager _stateManager;

    private readonly BufferWriter<byte> _bufferWriter = BufferWriter<byte>.Create(1024 * 64);
    private bool _isCanceled;
    private bool _isCompleted;
    private CancellationTokenSource? _flushCts;
    private Memory<byte> _pipeId = new byte[sizeof(ushort)];
    private int _chunkSize;
    private readonly SemaphoreSlim _pauseSemaphore = new SemaphoreSlim(0, 1);
    private bool _pauseWriting;
    private INexusLogger? _logger;
    private INexusSession? _session;

    public NexusPipeWriter(IPipeStateManager stateManager)
    {
        _stateManager = stateManager;
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
            if (value == false)
                _pauseSemaphore.Release();
        }
    }

    /// <summary>
    /// Sets up the pipe writer for use.
    /// </summary>
    public void Setup(INexusSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = session.Logger;
        _chunkSize = _session.Config.NexusPipeFlushChunkSize;
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

        if (_session == null)
            return default;

        if (_stateManager.UpdateState(_session.IsServer
                ? NexusDuplexPipe.State.ClientReaderServerWriterComplete
                : NexusDuplexPipe.State.ClientWriterServerReaderComplete))
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

        BitConverter.TryWriteBytes(_pipeId.Span, _stateManager.Id);

        var buffer = _bufferWriter.GetBuffer();

        var multiPartSend = bufferLength > _chunkSize;

        var sendingBuffer = multiPartSend
            ? buffer.Slice(0, _chunkSize)
            : buffer;

        var flushPosition = 0;

        if (_session == null)
            throw new InvalidOperationException("Session is null.");

        while (true)
        {
            if (_isCanceled)
                break;

            try
            {
                await _session.SendHeaderWithBody(
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
                _logger?.LogError(e, $"Unknown error while writing to pipe on Invocation Id: {_stateManager.Id}.");
                await _session.DisconnectAsync(DisconnectReason.ProtocolError).ConfigureAwait(false);
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
            if (_stateManager.UpdateState(_session.IsServer
                    ? NexusDuplexPipe.State.ClientReaderServerWriterComplete
                    : NexusDuplexPipe.State.ClientWriterServerReaderComplete))
                await _stateManager.NotifyState().ConfigureAwait(false);

            //-------------------------- await _nexusDuplexPipe.NotifyState();
        }

        return new FlushResult(_isCanceled, _isCompleted);
    }
}
