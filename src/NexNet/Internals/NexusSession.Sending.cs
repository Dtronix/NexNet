using System.Buffers;
using NexNet.Messages;
using NexNet.Invocation;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Threading;
using System;
using MemoryPack;
using Pipelines.Sockets.Unofficial.Arenas;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNet.Internals;

internal partial class NexusSession<TNexus, TProxy> : INexusSession<TProxy>
    where TNexus : NexusBase<TProxy>, IMethodInvoker, IInvocationMethodHash
    where TProxy : ProxyInvocationBase, IProxyInvoker, IInvocationMethodHash, new()
{
   
    public async ValueTask SendMessage<TMessage>(TMessage body, CancellationToken cancellationToken = default)
        where TMessage : IMessageBase
    {
        // | MessageType | Body Length | Body   |
        // |-------------|-------------|--------|
        // | byte        | ushort      | byte[] |

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
        _config.InternalOnSend?.Invoke(this, buffer.ToArray());
        buffer.CopyTo(_pipeOutput.GetSpan(length));
        _bufferWriter.Deallocate(buffer);
        _pipeOutput.Advance(length);

        _config.Logger?.LogTrace($"Sending {TMessage.Type} message & body with {length} bytes.");

        var result = await _pipeOutput.FlushAsync(cancellationToken).ConfigureAwait(false);

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.ProtocolError, false).ConfigureAwait(false);
    }


    public ValueTask SendHeaderWithBody(MessageType type, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
    {
        return SendHeaderWithBody(type, null, body, cancellationToken);
    }

    public async ValueTask SendHeaderWithBody(MessageType type, ReadOnlyMemory<byte>? messageHeader, ReadOnlySequence<byte> body, CancellationToken cancellationToken = default)
    {
        // | MessageType | Body Length | Message Header? | Body   |
        // |-------------|-------------|-----------------|--------|
        // | byte        | ushort      | byte[]?         | byte[] |

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

        _config.Logger?.LogTrace($"Sending {length} bytes with header and length.");
        FlushResult result = default;
        try
        {
            result = await _pipeOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {

        }

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.ProtocolError, false).ConfigureAwait(false);
    }


    public ValueTask SendHeader(MessageType type, CancellationToken cancellationToken = default)
    {
        return SendHeaderCore(type, false, cancellationToken);
    }

    /// <summary>
    /// Sends the the specified message header over the transport.
    /// </summary>
    /// <param name="type">Type of header to send.</param>
    /// <param name="force">Will the header even when the state set to Connected.</param>
    /// <param name="cancellationToken">Cancels the sending.</param>
    /// <returns>Task which competes upon successful sending.</returns>
    /// <exception cref="InvalidOperationException">Failed to acquire write lock.</exception>
    private async ValueTask SendHeaderCore(MessageType type, bool force, CancellationToken cancellationToken = default)
    {
        // | MessageType |
        // |-------------|
        // | byte        |

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

        _config.Logger?.LogTrace($"Sending {type} header.");
        FlushResult result = default;
        try
        {
            result = await _pipeOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {

        }

        OnSent?.Invoke();

        if (result.IsCanceled || result.IsCompleted)
            await DisconnectCore(DisconnectReason.ProtocolError, false).ConfigureAwait(false);
    }
}
