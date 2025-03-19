// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace NexNet.Transports.HttpSocket.Internals;
#if TARGET_BROWSER
    internal sealed class HttpSocketHandle
    {
        private HttpSocketState _state = HttpSocketState.Connecting;
#pragma warning disable CA1822 // Mark members as static
        public HttpStatusCode HttpStatusCode => (HttpStatusCode)0;
#pragma warning restore CA1822 // Mark members as static

        public IReadOnlyDictionary<string, IEnumerable<string>>? HttpResponseHeaders { get; set; }

        public HttpSocket? HttpSocket { get; private set; }
        public HttpSocketState State => HttpSocket?.State ?? _state;

        public static ClientHttpSocketOptions CreateDefaultOptions() => new ClientHttpSocketOptions();

        public void Dispose()
        {
            _state = HttpSocketState.Closed;
            HttpSocket?.Dispose();
        }

        public void Abort()
        {
            _state = HttpSocketState.Aborted;
            HttpSocket?.Abort();
        }

        public Task ConnectAsync(Uri uri, HttpMessageInvoker? _ /*invoker*/, CancellationToken cancellationToken, ClientHttpSocketOptions options)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ws = new BrowserHttpSocket();
            HttpSocket = ws;
            return ws.ConnectAsync(uri, options.RequestedSubProtocols, cancellationToken);
        }
    }
#endif