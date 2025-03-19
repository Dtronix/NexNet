// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.HttpSocket.Internals;

public abstract class HttpSocket : IDisposable
{
    public abstract HttpSocketCloseStatus? CloseStatus { get; }
    public abstract string? CloseStatusDescription { get; }
    public abstract string? SubProtocol { get; }
    public abstract HttpSocketState State { get; }

    public abstract void Abort();
    public abstract Task CloseAsync(HttpSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);
    public abstract Task CloseOutputAsync(HttpSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);
    public abstract void Dispose();
    public abstract Task<HttpSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
        CancellationToken cancellationToken);
    public abstract Task SendAsync(ArraySegment<byte> buffer,
        HttpSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    public virtual async ValueTask<ValueHttpSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> arraySegment))
        {
            HttpSocketReceiveResult r = await ReceiveAsync(arraySegment, cancellationToken).ConfigureAwait(false);
            return new ValueHttpSocketReceiveResult(r.Count, r.MessageType, r.EndOfMessage);
        }

        byte[] array = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            HttpSocketReceiveResult r = await ReceiveAsync(new ArraySegment<byte>(array, 0, buffer.Length), cancellationToken).ConfigureAwait(false);
            new Span<byte>(array, 0, r.Count).CopyTo(buffer.Span);
            return new ValueHttpSocketReceiveResult(r.Count, r.MessageType, r.EndOfMessage);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    public virtual ValueTask SendAsync(ReadOnlyMemory<byte> buffer, HttpSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> arraySegment) ?
            new ValueTask(SendAsync(arraySegment, messageType, endOfMessage, cancellationToken)) :
            SendWithArrayPoolAsync(buffer, messageType, endOfMessage, cancellationToken);

    public virtual ValueTask SendAsync(ReadOnlyMemory<byte> buffer, HttpSocketMessageType messageType, HttpSocketMessageFlags messageFlags, CancellationToken cancellationToken = default)
    {
        return SendAsync(buffer, messageType, messageFlags.HasFlag(HttpSocketMessageFlags.EndOfMessage), cancellationToken);
    }

    private async ValueTask SendWithArrayPoolAsync(
        ReadOnlyMemory<byte> buffer,
        HttpSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        byte[] array = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.Span.CopyTo(array);
            await SendAsync(new ArraySegment<byte>(array, 0, buffer.Length), messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    public static TimeSpan DefaultKeepAliveInterval
    {
        // In the .NET Framework, this pulls the value from a P/Invoke.  Here we just hardcode it to a reasonable default.
        get { return HttpSocketDefaults.DefaultClientKeepAliveInterval; }
    }

    protected static void ThrowOnInvalidState(HttpSocketState state, params HttpSocketState[] validStates)
    {
        string validStatesText = string.Empty;

        if (validStates != null && validStates.Length > 0)
        {
            foreach (HttpSocketState currentState in validStates)
            {
                if (state == currentState)
                {
                    return;
                }
            }

            validStatesText = string.Join(", ", validStates);
        }

        throw new HttpSocketException(HttpSocketError.InvalidState,  string.Format(Strings.net_HttpSockets_InvalidState, state, validStatesText));
    }

    protected static bool IsStateTerminal(HttpSocketState state) =>
        state == HttpSocketState.Closed || state == HttpSocketState.Aborted;

    public static ArraySegment<byte> CreateClientBuffer(int receiveBufferSize, int sendBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBufferSize);
        return new ArraySegment<byte>(new byte[Math.Max(receiveBufferSize, sendBufferSize)]);
    }

    public static ArraySegment<byte> CreateServerBuffer(int receiveBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBufferSize);
        return new ArraySegment<byte>(new byte[receiveBufferSize]);
    }

    /// <summary>Creates a <see cref="HttpSocket"/> that operates on a <see cref="Stream"/> representing a web socket connection.</summary>
    /// <param name="stream">The <see cref="Stream"/> for the connection.</param>
    /// <param name="isServer"><code>true</code> if this is the server-side of the connection; <code>false</code> if it's the client side.</param>
    /// <param name="subProtocol">The agreed upon sub-protocol that was used when creating the connection.</param>
    /// <param name="keepAliveInterval">The keep-alive interval to use, or <see cref="Timeout.InfiniteTimeSpan"/> to disable keep-alives.</param>
    /// <returns>The created <see cref="HttpSocket"/>.</returns>
    public static HttpSocket CreateFromStream(Stream stream, bool isServer, string? subProtocol, TimeSpan keepAliveInterval)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException(!stream.CanRead ? Strings.NotReadableStream : Strings.NotWriteableStream, nameof(stream));
        }

        if (subProtocol != null)
        {
            HttpSocketValidate.ValidateSubprotocol(subProtocol);
        }

        if (keepAliveInterval != Timeout.InfiniteTimeSpan && keepAliveInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(keepAliveInterval), keepAliveInterval,
                 string.Format(Strings.net_HttpSockets_ArgumentOutOfRange_TooSmall, 0));
        }

        return new ManagedHttpSocket(stream, isServer, subProtocol, keepAliveInterval, HttpSocketDefaults.DefaultKeepAliveTimeout);
    }

    /// <summary>Creates a <see cref="HttpSocket"/> that operates on a <see cref="Stream"/> representing a web socket connection.</summary>
    /// <param name="stream">The <see cref="Stream"/> for the connection.</param>
    /// <param name="options">The options with which the websocket must be created.</param>
    public static HttpSocket CreateFromStream(Stream stream, HttpSocketCreationOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException(!stream.CanRead ? Strings.NotReadableStream : Strings.NotWriteableStream, nameof(stream));

        return new ManagedHttpSocket(stream, options);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.")]
    public static bool IsApplicationTargeting45() => true;

    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("This API supports the .NET Framework infrastructure and is not intended to be used directly from your code.")]
    public static void RegisterPrefixes()
    {
        // The current WebRequest implementation in the libraries does not support upgrading
        // web socket connections.  For now, we throw.
        throw new PlatformNotSupportedException();
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static HttpSocket CreateClientHttpSocket(Stream innerStream,
        string? subProtocol, int receiveBufferSize, int sendBufferSize,
        TimeSpan keepAliveInterval, bool useZeroMaskingKey, ArraySegment<byte> internalBuffer)
    {
        ArgumentNullException.ThrowIfNull(innerStream);

        if (!innerStream.CanRead || !innerStream.CanWrite)
        {
            throw new ArgumentException(!innerStream.CanRead ? Strings.NotReadableStream : Strings.NotWriteableStream, nameof(innerStream));
        }

        if (subProtocol != null)
        {
            HttpSocketValidate.ValidateSubprotocol(subProtocol);
        }

        if (keepAliveInterval != Timeout.InfiniteTimeSpan && keepAliveInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(keepAliveInterval), keepAliveInterval,
                 string.Format(Strings.net_HttpSockets_ArgumentOutOfRange_TooSmall, 0));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBufferSize);

        // Ignore useZeroMaskingKey. ManagedHttpSocket doesn't currently support that debugging option.
        // Ignore internalBuffer. ManagedHttpSocket uses its own small buffer for headers/control messages.
        return new ManagedHttpSocket(innerStream, false, subProtocol, keepAliveInterval, HttpSocketDefaults.DefaultKeepAliveTimeout);
    }
}