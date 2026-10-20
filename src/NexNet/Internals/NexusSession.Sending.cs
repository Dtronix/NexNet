using System.Buffers;
using NexNet.Messages;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using NexNet.Serialization;
using NexNet.Logging;
using NexNet.Internals.Pipelines.Arenas;
using System.Collections.Generic;

namespace NexNet.Internals;

internal partial class NexusSession<TNexus, TProxy>
{

    /// <summary>
    /// Asynchronously sends a message of type <typeparamref name="TMessage"/> over the network.
    /// </summary>
    /// <typeparam name="TMessage">The type of the message to be sent.</typeparam>
    /// <param name="body">The message to be sent.</param>
    /// <param name="cancellationToken">An optional cancellation token to observe while waiting for the task to complete.</param>
    /// <remarks>
    /// Sends with the following format:
    /// | Field           | Size (bytes) | Description                                                                 |
    /// |-----------------|--------------|-----------------------------------------------------------------------------|
    /// | Type            | 1            | The type of the message.                                                    |
    /// | Content Length  | 2            | The length of the body of the message.                                      |
    /// | Body            | Variable     | The body of the message. Its length is specified by the 'Content Length'.   |
    /// </remarks>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    public async ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
        where TMessage : IMessageBase
    {
        if (_pipeOutput == null || cancellationToken.IsCancellationRequested)
            return;

        if (State != ConnectionState.Connected)
            return;

        // Released by hand: a large message gives the mutex up to stream its frames (see SendLargeMessage).
        var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);
        var mutexHeld = true;
        try
        {
            if (mutexResult.Success != true)
                throw new InvalidOperationException("Could not acquire write lock");

            // The session may have disconnected while waiting for the lock; disconnect clears _pipeOutput without it.
            var output = _pipeOutput;
            if (output == null || State != ConnectionState.Connected)
                return;

            int length;
            LargeMessage large = default;
            try
            {
                var header = _bufferWriter.GetMemory(3);
                _bufferWriter.Advance(3);
                var bodyWriter = new MsgPackWriter(_bufferWriter);
                body.Serialize(ref bodyWriter);
                bodyWriter.Flush();

                // Checked before anything reaches the output, so an oversized message fails alone. Arguments and
                // results larger than one frame are streamed.
                var bodyLength = _bufferWriter.Length - 3;
                if (bodyLength > ushort.MaxValue)
                {
                    large = StageLargeMessage(body, bodyLength);
                    length = 0;
                }
                else
                {
                    header.Span[0] = (byte)TMessage.Type;

                    BinaryPrimitives.WriteUInt16LittleEndian(header.Span.Slice(1, 2), (ushort)bodyLength);

                    length = (int)_bufferWriter.Length;
                    var buffer = _bufferWriter.GetBuffer();

                    // Only used for debugging
                    _config.InternalOnSend?.Invoke(this, buffer.ToArray());

                    buffer.CopyTo(output.GetSpan(length));
                    output.Advance(length);
                }
            }
            catch (InvalidOperationException) when (State != ConnectionState.Connected)
            {
                // Disconnect completed the output while this send was in progress.
                return;
            }
            finally
            {
                _bufferWriter.Reset();
            }

            if (large.Buffer != null)
            {
                mutexResult.Dispose();
                mutexHeld = false;
                await SendLargeMessage(large, cancellationToken).ConfigureAwait(false);
                return;
            }

            Logger?.LogTrace($"Sending {TMessage.Type} header and body with {length} total bytes.");

            // Register a cancellation token to cancel the flush operation.
            CancellationTokenRegistration? ctRegistration = null;
            if (cancellationToken.CanBeCanceled)
            {
                ctRegistration = cancellationToken.Register(static obj =>
                {
                    Unsafe.As<PipeWriter?>(obj)?.CancelPendingFlush();
                }, output, false);
            }

            FlushResult result = default;
            try
            {
                // ReSharper disable once MethodSupportsCancellation
                result = await FlushOutput(output).ConfigureAwait(false);

                // Return if the operation was canceled.
                if (result.IsCanceled)
                    return;
            }
            catch (ObjectDisposedException)
            {
                // noop
            }
            catch (InvalidOperationException) when (State != ConnectionState.Connected)
            {
                // Disconnect completed the output while this send was in progress.
                return;
            }
            finally
            {
                if (ctRegistration != null)
                    await ctRegistration.Value.DisposeAsync().ConfigureAwait(false);
            }

            OnSent?.Invoke();

            if (result.IsCompleted)
                await DisconnectCore(DisconnectReason.SocketClosedWhenWriting, false).ConfigureAwait(false);
        }
        finally
        {
            if (mutexHeld)
                mutexResult.Dispose();
        }
    }

    /// <summary>
    /// A message body staged for streaming (docs/internals/flow-control-v2.md, section 4.3).
    /// </summary>
    private readonly record struct LargeMessage(byte[]? Buffer, int Length, ushort InvocationId, LargeMessageKind Kind, MessageType InnerType);

    /// <summary>
    /// Copies a body larger than one frame out of the shared buffer so it can be streamed. Only arguments and results
    /// can be; anything larger than the peer accepts is refused before anything is sent.
    /// </summary>
    private LargeMessage StageLargeMessage<TMessage>(TMessage body, long bodyLength)
        where TMessage : IMessageBase
    {
        (ushort Id, LargeMessageKind Kind) key = body switch
        {
            InvocationMessage invocation => (invocation.InvocationId, LargeMessageKind.Argument),
            InvocationResultMessage result => (result.InvocationId, LargeMessageKind.Result),
            _ => throw new NexusMessageTooLargeException(bodyLength, ushort.MaxValue),
        };

        if (bodyLength > SendMaxMessageSize)
            throw new NexusMessageTooLargeException(bodyLength, SendMaxMessageSize);

        var buffer = ArrayPool<byte>.Shared.Rent((int)bodyLength);
        _bufferWriter.GetBuffer().Slice(3).CopyTo(buffer);
        return new LargeMessage(buffer, (int)bodyLength, key.Id, key.Kind, TMessage.Type);
    }

    /// <summary>
    /// Streams a staged body as MessageStart and MessageChunk frames, taking the write mutex once per frame so other
    /// senders interleave. Stops on the receiver's abort (answering with Ack) or the caller's cancellation (sending
    /// Cancelled); no chunk follows either.
    /// </summary>
    private async ValueTask SendLargeMessage(LargeMessage message, CancellationToken cancellationToken)
    {
        var key = message.InvocationId | ((int)message.Kind << 16);
        var stream = new OutgoingLargeMessage();
        try
        {
            if (!_outgoingLargeMessages.TryAdd(key, stream))
                throw new InvalidOperationException($"A large {message.Kind} for invocation {message.InvocationId} is already being sent.");

            using (var start = _poolManager.Rent<MessageStartMessage>())
            {
                start.InvocationId = message.InvocationId;
                start.Kind = message.Kind;
                start.InnerType = message.InnerType;
                start.TotalLength = (uint)message.Length;
                await SendMessage(start).ConfigureAwait(false);
            }

            var header = new byte[3];
            BinaryPrimitives.WriteUInt16LittleEndian(header, message.InvocationId);
            header[2] = (byte)message.Kind;

            for (var offset = 0; offset < message.Length;)
            {
                if (State != ConnectionState.Connected)
                    return;

                if (stream.Aborted)
                {
                    await SendAbort(message.InvocationId, message.Kind, LargeMessageAbortReason.Ack).ConfigureAwait(false);
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    await SendAbort(message.InvocationId, message.Kind, LargeMessageAbortReason.Cancelled).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var count = Math.Min(message.Length - offset, ushort.MaxValue);
                await SendHeaderWithBody(MessageType.MessageChunk, header, new ReadOnlySequence<byte>(message.Buffer!, offset, count)).ConfigureAwait(false);
                offset += count;
            }
        }
        finally
        {
            _outgoingLargeMessages.TryRemove(new KeyValuePair<int, OutgoingLargeMessage>(key, stream));
            ArrayPool<byte>.Shared.Return(message.Buffer!);
        }
    }

    private async ValueTask SendAbort(ushort invocationId, LargeMessageKind kind, LargeMessageAbortReason reason)
    {
        using var abort = _poolManager.Rent<MessageAbortMessage>();
        abort.InvocationId = invocationId;
        abort.Kind = kind;
        abort.Reason = reason;
        await SendMessage(abort).ConfigureAwait(false);
    }

    /// <summary>
    /// A large message this side is sending; the receiver can abort it.
    /// </summary>
    private sealed class OutgoingLargeMessage
    {
        private int _aborted;
        public bool Aborted => Volatile.Read(ref _aborted) == 1;
        public void Abort() => Volatile.Write(ref _aborted, 1);
    }

    public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
    {
        return SendHeaderWithBody(type, null, body, true, cancellationToken);
    }

    public ValueTask SendHeaderWithBody(
        MessageType type,
        ReadOnlyMemory<byte>? messageHeader,
        ReadOnlySequence<byte> body,
        CancellationToken cancellationToken = default)
    {
        return SendHeaderWithBody(type, messageHeader, body, true, cancellationToken);
    }

    /// <summary>
    /// Flushes the transport, recording when the flush started so a flush that never completes is detected as a
    /// stall (<see cref="NexNet.Transports.ConfigBase.ProgressTimeout"/>). Called with the write mutex held.
    /// </summary>
    private async ValueTask<FlushResult> FlushOutput(PipeWriter output)
    {
        Volatile.Write(ref _flushStartedTicks, _config.Time.GetTickCount64());
        try
        {
            // ReSharper disable once MethodSupportsCancellation
            return await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _flushStartedTicks, 0);
        }
    }

    /// <summary>
    /// Asynchronously sends a message with a header and body over the network.
    /// </summary>
    /// <param name="type">The type of the message to be sent.</param>
    /// <param name="messageHeader">The header of the message. Its length can vary. Can be null.</param>
    /// <param name="body">The body of the message.</param>
    /// <param name="flush">False to leave the frame in the transport's buffer for a later flush.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation.</param>
    /// <remarks>
    /// Sends with the following format:
    /// | Field           | Size (bytes) | Description                                                                       |
    /// |-----------------|--------------|-----------------------------------------------------------------------------------|
    /// | Type            | 1            | The type of the message.                                                          |
    /// | Content Length  | 2            | The length of the body of the message.                                            |
    /// | Message Header  | Variable     | The header of the message. Its length can vary and must be known by the receiver.                             |
    /// | Body            | Variable     | The body of the message. Its length is specified by the 'Content Length'. 
    /// </remarks>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    public async ValueTask SendHeaderWithBody(
        MessageType type,
        ReadOnlyMemory<byte>? messageHeader,
        ReadOnlySequence<byte> body,
        bool flush,
        CancellationToken cancellationToken = default)
    {
        if (_pipeOutput == null || cancellationToken.IsCancellationRequested)
            return;

        if (State != ConnectionState.Connected)
            return;

        using var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);

        if (mutexResult.Success != true)
            throw new InvalidOperationException("Could not acquire write lock");

        // The session may have disconnected while waiting for the lock; disconnect clears _pipeOutput without it.
        var output = _pipeOutput;
        if (output == null || State != ConnectionState.Connected)
            return;

        if (body.Length > ushort.MaxValue)
            throw new NexusMessageTooLargeException(body.Length, ushort.MaxValue);

        var length = (int)body.Length;
        var contentLength = (ushort)length;

        var headerLength = 3 + (messageHeader?.Length ?? 0);

        try
        {
            var header = output.GetMemory(headerLength);
            header.Span[0] = (byte)type;
            BinaryPrimitives.WriteUInt16LittleEndian(header.Span.Slice(1, 2), contentLength);

            // Copy the message header
            messageHeader?.CopyTo(header.Slice(3));

            output.Advance(headerLength);
            body.CopyTo(output.GetSpan((int)body.Length));
            output.Advance(length);
        }
        catch (InvalidOperationException) when (State != ConnectionState.Connected)
        {
            // Disconnect completed the output while this send was in progress.
            return;
        }

        if (!flush)
        {
            _config.InternalOnSend?.Invoke(this, DebugCopy(type, contentLength, body));
            return;
        }

        // Register a cancellation token to cancel the flush operation.
        CancellationTokenRegistration? ctRegistration = null;
        if (cancellationToken.CanBeCanceled)
        {
            ctRegistration = cancellationToken.Register(static obj =>
            {
                Unsafe.As<PipeWriter?>(obj)?.CancelPendingFlush();
            }, output, false);
        }

        if (_config.InternalOnSend != null)
            _config.InternalOnSend?.Invoke(this, DebugCopy(type, contentLength, body));

        Logger?.LogTrace($"Sending {type} header and {length} total bytes.");
        FlushResult result = default;
        try
        {
            // ReSharper disable once MethodSupportsCancellation
            result = await FlushOutput(output).ConfigureAwait(false);

            // Return if the operation was canceled.
            if (result.IsCanceled)
                return;
        }
        catch (ObjectDisposedException)
        {
            // noop
        }
        catch (InvalidOperationException) when (State != ConnectionState.Connected)
        {
            // Disconnect completed the output while this send was in progress.
            return;
        }
        catch (Exception e)
        {
            Logger?.LogCritical(e, "Error while flushing");
        }
        finally
        {
            if (ctRegistration != null)
                await ctRegistration.Value.DisposeAsync().ConfigureAwait(false);
        }

        OnSent?.Invoke();

        if (result.IsCompleted)
            await DisconnectCore(DisconnectReason.SocketClosedWhenWriting, false).ConfigureAwait(false);
    }
    
    // Only used for debugging (InternalOnSend).
    private static byte[] DebugCopy(MessageType type, ushort contentLength, ReadOnlySequence<byte> body)
    {
        var debugCopy = new byte[body.Length + 3];
        debugCopy[0] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(debugCopy).Slice(1, 2), contentLength);
        body.CopyTo(new Span<byte>(debugCopy).Slice(3));
        return debugCopy;
    }

    /// <summary>
    /// Asynchronously sends raw data over the transport. Does nothing if the session is not connected.
    /// </summary>
    /// <param name="data">The data to send..</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    /// <remarks>
    /// Sends with the following format:
    /// | Field           | Size (bytes) | Description                                                                 |
    /// |-----------------|--------------|--------------------------------|
    /// | Data            | Varies       | The message data.            
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the write lock cannot be acquired.</exception>
    public ValueTask SendRaw(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        return SendHeaderCore(MessageType.Unset, data, true, cancellationToken);
    }

    /// <summary>
    /// Asynchronously sends a message header of a specified type over the transport.
    /// Does nothing if the session is not connected.
    /// </summary>
    /// <param name="type">The type of the message header to be sent.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    /// <remarks>
    /// Sends with the following format:
    /// | Field           | Size (bytes) | Description                                                                 |
    /// |-----------------|--------------|--------------------------------|
    /// | Type            | 1            | The type of the message.            
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the write lock cannot be acquired.</exception>
    public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
    {
        return SendHeaderCore(type, null, false, cancellationToken);
    }
    
    /// <summary>
    /// Asynchronously sends a message header of a specified type over the transport with the passed data.
    /// Does nothing if the session is not connected.
    /// </summary>
    /// <param name="type">The type of the message header to be sent.</param>
    /// <param name="data">Optional data to send immediately after the header type.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    /// <remarks>
    /// Sends with the following format:
    /// | Field           | Size (bytes) | Description                                                                 |
    /// |-----------------|--------------|--------------------------------|
    /// | Type            | 1            | The type of the message.
    /// | byte[]          | Varies       | The data sent after the header      
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the write lock cannot be acquired.</exception>
    public ValueTask SendHeader(MessageType type, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        return SendHeaderCore(type, data, false, cancellationToken);
    }

    /// <summary>
    /// Asynchronously sends a message header of a specified type over the transport.
    /// </summary>
    /// <param name="type">The type of the message header to be sent.</param>
    /// <param name="postHeaderData">Optional data to send immediately after the header type.</param>
    /// <param name="force">If set to true, the header will be sent even when the connection state is not set to Connected.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    /// <remarks>
    /// Sends with the following format:
    /// | Field             | Size (bytes) | Description                                                                 |
    /// |-------------------|--------------|--------------------------------|
    /// | Type              | 1            | The type of the message.
    /// | byte[] (OPTIONAL) | Varies       | The optional data sent after the header       
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the write lock cannot be acquired.</exception>
    private async ValueTask SendHeaderCore(MessageType type, ReadOnlyMemory<byte>? postHeaderData, bool force, CancellationToken cancellationToken = default)
    {
        if (_pipeOutput == null || cancellationToken.IsCancellationRequested)
            return;

        //if (State != ConnectionState.Connected && State != ConnectionState.Disconnecting)
        if (State != ConnectionState.Connected && !force)
            return;
        
        using var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);

        if (mutexResult.Success != true)
            throw new InvalidOperationException("Could not acquire write lock");

        // The session may have disconnected while waiting for the lock; disconnect clears _pipeOutput without it.
        var output = _pipeOutput;
        if (output == null)
            return;

        try
        {
            if (type != MessageType.Unset)
            {
                output.GetSpan(1)[0] = (byte)type;
                output.Advance(1);
                _config.InternalOnSend?.Invoke(this, new[] { (byte)type });
                Logger?.LogTrace($"Sending {type} header.");
            }
            else
            {
                Logger?.LogTrace("Sending raw data.");
            }

            // Check the post header data.
            if (postHeaderData != null)
            {
                var data = postHeaderData.Value;
                var length = data.Length;
                data.Span.CopyTo(output.GetSpan(length));
                output.Advance(length);
                _config.InternalOnSend?.Invoke(this, data.ToArray());
            }
        }
        catch (InvalidOperationException) when (State != ConnectionState.Connected)
        {
            // Disconnect completed the output while this send was in progress.
            return;
        }

        // Register a cancellation token to cancel the flush operation.
        CancellationTokenRegistration? ctRegistration = null;
        if (cancellationToken.CanBeCanceled)
        {
            ctRegistration = cancellationToken.Register(static obj =>
            {
                Unsafe.As<PipeWriter?>(obj)?.CancelPendingFlush();
            }, output, false);
        }

        FlushResult result = default;
        try
        {
            // ReSharper disable once MethodSupportsCancellation
            result = await FlushOutput(output).ConfigureAwait(false);

            // Return if the operation was canceled.
            if (result.IsCanceled)
                return;
        }
        catch (ObjectDisposedException)
        {
            // noop
        }
        catch (InvalidOperationException) when (State != ConnectionState.Connected)
        {
            // Disconnect completed the output while this send was in progress.
            return;
        }
        finally
        {
            if (ctRegistration != null)
                await ctRegistration.Value.DisposeAsync().ConfigureAwait(false);
        }

        OnSent?.Invoke();

        if (result.IsCompleted)
            await DisconnectCore(DisconnectReason.SocketClosedWhenWriting, false).ConfigureAwait(false);
    }
}
