// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.HttpSocket.Internals;

public sealed partial class ClientHttpSocket : HttpSocket
{
    /// <summary>This is really an InternalState value, but Interlocked doesn't support operations on values of enum types.</summary>
    private InternalState _state;
    private HttpSocketHandle? _innerHttpSocket;

    public ClientHttpSocket()
    {
        _state = InternalState.Created;
        Options = HttpSocketHandle.CreateDefaultOptions();
    }

    public ClientHttpSocketOptions Options { get; }

    public override HttpSocketCloseStatus? CloseStatus => _innerHttpSocket?.HttpSocket?.CloseStatus;

    public override string? CloseStatusDescription => _innerHttpSocket?.HttpSocket?.CloseStatusDescription;

    public override string? SubProtocol => _innerHttpSocket?.HttpSocket?.SubProtocol;

    public override HttpSocketState State
    {
        get
        {
            // state == Connected or Disposed
            if (_innerHttpSocket != null)
            {
                return _innerHttpSocket.State;
            }

            switch (_state)
            {
                case InternalState.Created:
                    return HttpSocketState.None;
                case InternalState.Connecting:
                    return HttpSocketState.Connecting;
                default: // We only get here if disposed before connecting
                    Debug.Assert(_state == InternalState.Disposed);
                    return HttpSocketState.Closed;
            }
        }
    }

    /// <summary>
    /// Gets the upgrade response status code if <see cref="ClientHttpSocketOptions.CollectHttpResponseDetails" /> is set.
    /// </summary>
    public System.Net.HttpStatusCode HttpStatusCode => _innerHttpSocket?.HttpStatusCode ?? 0;

    /// <summary>
    /// Gets the upgrade response headers if <see cref="ClientHttpSocketOptions.CollectHttpResponseDetails" /> is set.
    /// The setter may be used to reduce the memory usage of an active HttpSocket connection once headers are no longer needed.
    /// </summary>
    public IReadOnlyDictionary<string, IEnumerable<string>>? HttpResponseHeaders
    {
        get => _innerHttpSocket?.HttpResponseHeaders;
        set
        {
            if (_innerHttpSocket != null)
            {
                _innerHttpSocket.HttpResponseHeaders = value;
            }
        }
    }

    /// <summary>
    /// Connects to a HttpSocket server as an asynchronous operation.
    /// </summary>
    /// <param name="uri">The URI of the HttpSocket server to connect to.</param>
    /// <param name="cancellationToken">A cancellation token used to propagate notification that the operation should be canceled.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        return ConnectAsync(uri, null, cancellationToken);
    }

    /// <summary>
    /// Connects to a HttpSocket server as an asynchronous operation.
    /// </summary>
    /// <param name="uri">The URI of the HttpSocket server to connect to.</param>
    /// <param name="invoker">The <see cref="HttpMessageInvoker" /> instance to use for connecting.</param>
    /// <param name="cancellationToken">A cancellation token used to propagate notification that the operation should be canceled.</param>
    /// <returns>The task object representing the asynchronous operation.</returns>
    public Task ConnectAsync(Uri uri, HttpMessageInvoker? invoker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException(Strings.net_uri_NotAbsolute, nameof(uri));
        }
        if (uri.Scheme != UriScheme.Ws && uri.Scheme != UriScheme.Wss)
        {
            throw new ArgumentException(Strings.net_HttpSockets_Scheme, nameof(uri));
        }

        // Check that we have not started already.
        switch (Interlocked.CompareExchange(ref _state, InternalState.Connecting, InternalState.Created))
        {
            case InternalState.Disposed:
                throw new ObjectDisposedException(GetType().FullName);

            case InternalState.Created:
                break;

            default:
                throw new InvalidOperationException(Strings.net_HttpSockets_AlreadyStarted);
        }

        Options.SetToReadOnly();
        return ConnectAsyncCore(uri, invoker, cancellationToken);
    }

    private async Task ConnectAsyncCore(Uri uri, HttpMessageInvoker? invoker, CancellationToken cancellationToken)
    {
        _innerHttpSocket = new HttpSocketHandle();

        try
        {
            await _innerHttpSocket.ConnectAsync(uri, invoker, cancellationToken, Options).ConfigureAwait(false);
        }
        catch
        {
            Dispose();
            throw;
        }

        if (Interlocked.CompareExchange(ref _state, InternalState.Connected, InternalState.Connecting) != InternalState.Connecting)
        {
            Debug.Assert(_state == InternalState.Disposed);
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    public override Task SendAsync(ArraySegment<byte> buffer, HttpSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        ConnectedHttpSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, HttpSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        ConnectedHttpSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, HttpSocketMessageType messageType, HttpSocketMessageFlags messageFlags, CancellationToken cancellationToken) =>
        ConnectedHttpSocket.SendAsync(buffer, messageType, messageFlags, cancellationToken);

    public override Task<HttpSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        ConnectedHttpSocket.ReceiveAsync(buffer, cancellationToken);

    public override ValueTask<ValueHttpSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ConnectedHttpSocket.ReceiveAsync(buffer, cancellationToken);

    public override Task CloseAsync(HttpSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        ConnectedHttpSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override Task CloseOutputAsync(HttpSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        ConnectedHttpSocket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    private HttpSocket ConnectedHttpSocket
    {
        get
        {
            ObjectDisposedException.ThrowIf(_state == InternalState.Disposed, this);

            if (_state != InternalState.Connected)
            {
                throw new InvalidOperationException(Strings.net_HttpSockets_NotConnected);
            }

            Debug.Assert(_innerHttpSocket != null);
            Debug.Assert(_innerHttpSocket.HttpSocket != null);

            return _innerHttpSocket.HttpSocket;
        }
    }

    public override void Abort()
    {
        if (_state != InternalState.Disposed)
        {
            _innerHttpSocket?.Abort();
            Dispose();
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _state, InternalState.Disposed) != InternalState.Disposed)
        {
            _innerHttpSocket?.Dispose();
        }
    }

    private enum InternalState
    {
        Created = 0,
        Connecting = 1,
        Connected = 2,
        Disposed = 3
    }
}
