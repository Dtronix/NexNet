using System.Buffers;
using NexNet.Messages;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System;
using MemoryPack;
using Pipelines.Sockets.Unofficial.Arenas;

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

        using var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);

        var header = _bufferWriter.GetMemory(3);
        _bufferWriter.Advance(3);
        MemoryPackSerializer.Serialize(_bufferWriter, body);

        var contentLength = checked((ushort)(_bufferWriter.Length - 3));

        header.Span[0] = (byte)TMessage.Type;

        BitConverter.TryWriteBytes(header.Span.Slice(1, 2), contentLength);

        var length = (int)_bufferWriter.Length;
        var buffer = _bufferWriter.GetBuffer();

        // Only used for debugging
        _config.InternalOnSend?.Invoke(this, buffer.ToArray());

        buffer.CopyTo(_pipeOutput.GetSpan(length));
        _bufferWriter.Reset();
        _pipeOutput.Advance(length);

        Logger?.LogTrace($"Sending {TMessage.Type} header and  body with {length} total bytes.");

        var result = await _pipeOutput.FlushAsync().ConfigureAwait(false);

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.SocketClosedWhenWriting, false).ConfigureAwait(false);
    }


    public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
    {
        return SendHeaderWithBody(type, null, body, cancellationToken);
    }

    /// <summary>
    /// Asynchronously sends a message with a header and body over the network.
    /// </summary>
    /// <param name="type">The type of the message to be sent.</param>
    /// <param name="messageHeader">The header of the message. Its length can vary. Can be null.</param>
    /// <param name="body">The body of the message.</param>
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
    public async ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
    {
        if (_pipeOutput == null || cancellationToken.IsCancellationRequested)
            return;

        if (State != ConnectionState.Connected)
            return;

        using var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);

        if (mutexResult.Success != true)
            throw new InvalidOperationException("Could not acquire write lock");
        var length = (int)body.Length;
        var contentLength = checked((ushort)(length));

        var headerLength = 3 + (messageHeader?.Length ?? 0);

        var header = _pipeOutput.GetMemory(headerLength);
        header.Span[0] = (byte)type;
        BitConverter.TryWriteBytes(header.Span.Slice(1, 2), contentLength);

        // Copy the message header
        messageHeader?.CopyTo(header.Slice(3));

        _pipeOutput.Advance(headerLength);
        body.CopyTo(_pipeOutput.GetSpan((int)body.Length));
        _pipeOutput.Advance(length);

        if (_config.InternalOnSend != null)
        {
            var debugCopy = new byte[body.Length + 3];
            debugCopy[0] = (byte)type;
            BitConverter.TryWriteBytes(new Span<byte>(debugCopy).Slice(1, 2), contentLength);
            body.CopyTo(new Span<byte>(debugCopy).Slice(3));
            _config.InternalOnSend?.Invoke(this, debugCopy);
        }

        Logger?.LogTrace($"Sending {type} header and {length} total bytes.");
        FlushResult result = default;
        try
        {
            result = await _pipeOutput.FlushAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {

        }

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.SocketClosedWhenWriting, false).ConfigureAwait(false);
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
        return SendHeaderCore(type, false, cancellationToken);
    }

    /// <summary>
    /// Asynchronously sends a message header of a specified type over the transport.
    /// </summary>
    /// <param name="type">The type of the message header to be sent.</param>
    /// <param name="force">If set to true, the header will be sent even when the connection state is not set to Connected.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    /// <remarks>
    /// Sends with the following format:
    /// | Field           | Size (bytes) | Description                                                                 |
    /// |-----------------|--------------|--------------------------------|
    /// | Type            | 1            | The type of the message.            
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the write lock cannot be acquired.</exception>
    private async ValueTask SendHeaderCore(MessageType type, bool force, CancellationToken cancellationToken = default)
    {

        if (_pipeOutput == null || cancellationToken.IsCancellationRequested)
            return;

        //if (State != ConnectionState.Connected && State != ConnectionState.Disconnecting)
        if (State != ConnectionState.Connected && !force)
            return;

        using var mutexResult = await _writeMutex.TryWaitAsync(cancellationToken).ConfigureAwait(false);

        if (mutexResult.Success != true)
            throw new InvalidOperationException("Could not acquire write lock");

        _config.InternalOnSend?.Invoke(this, new[] { (byte)type });

        _pipeOutput.GetSpan(1)[0] = (byte)type;
        _pipeOutput.Advance(1);

        Logger?.LogTrace($"Sending {type} header.");
        FlushResult result = default;
        try
        {
            // If the cancellation token is canceled after the flush has completed, QUIC throws sometimes.
            // https://github.com/dotnet/runtime/issues/82704
            result = await _pipeOutput.FlushAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {

        }

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.SocketClosedWhenWriting, false).ConfigureAwait(false);
    }
}
