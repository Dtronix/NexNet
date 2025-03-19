// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports.HttpSocket.Internals;
#if !TARGET_BROWSER
internal sealed class HttpSocketHandle
{
    // Shared, lazily-initialized invokers used to avoid some allocations when using default options.
    private static HttpMessageInvoker? s_defaultInvokerDefaultProxy;
    private static HttpMessageInvoker? s_defaultInvokerNoProxy;

    private readonly CancellationTokenSource _abortSource = new CancellationTokenSource();
    private HttpSocketState _state = HttpSocketState.Connecting;
    //private HttpSocketDeflateOptions? _negotiatedDeflateOptions;

    public HttpSocket? HttpSocket { get; private set; }
    public HttpSocketState State => HttpSocket?.State ?? _state;
    public HttpStatusCode HttpStatusCode { get; private set; }

    public IReadOnlyDictionary<string, IEnumerable<string>>? HttpResponseHeaders { get; set; }

    public static ClientHttpSocketOptions CreateDefaultOptions() => new ClientHttpSocketOptions() { Proxy = DefaultWebProxy.Instance };

    public void Dispose()
    {
        _state = HttpSocketState.Closed;
        HttpSocket?.Dispose();
    }

    public void Abort()
    {
        _abortSource.Cancel();
        HttpSocket?.Abort();
    }

    public async Task ConnectAsync(Uri uri, HttpMessageInvoker? invoker, CancellationToken cancellationToken, ClientHttpSocketOptions options)
    {
        bool disposeInvoker = false;
        if (invoker is null)
        {
            if (options.HttpVersion.Major >= 2 || options.HttpVersionPolicy == HttpVersionPolicy.RequestVersionOrHigher)
            {
                throw new ArgumentException(Strings.net_HttpSockets_CustomInvokerRequiredForHttp2, nameof(options));
            }

            invoker = SetupInvoker(options, out disposeInvoker);
        }
        else if (!options.AreCompatibleWithCustomInvoker())
        {
            // This will not throw if the Proxy is a DefaultWebProxy.
            throw new ArgumentException(Strings.net_HttpSockets_OptionsIncompatibleWithCustomInvoker, nameof(options));
        }

        HttpResponseMessage? response = null;
        bool disposeResponse = false;

        // force non-secure request to 1.1 whenever it is possible as HttpClient does
        bool tryDowngrade = uri.Scheme == UriScheme.Ws && (options.HttpVersion == HttpVersion.Version11 || options.HttpVersionPolicy == HttpVersionPolicy.RequestVersionOrLower);
        try
        {

            while (true)
            {
                try
                {
                    HttpRequestMessage request;
                    if (!tryDowngrade && options.HttpVersion >= HttpVersion.Version20
                        || (options.HttpVersion == HttpVersion.Version11 && options.HttpVersionPolicy == HttpVersionPolicy.RequestVersionOrHigher && uri.Scheme == UriScheme.Wss))
                    {
                        if (options.HttpVersion > HttpVersion.Version20 && options.HttpVersionPolicy != HttpVersionPolicy.RequestVersionOrLower)
                        {
                            throw new HttpSocketException(HttpSocketError.UnsupportedProtocol);
                        }
                        request = new HttpRequestMessage(HttpMethod.Connect, uri) { Version = HttpVersion.Version20 };
                        tryDowngrade = true;
                    }
                    else if (tryDowngrade || options.HttpVersion == HttpVersion.Version11)
                    {
                        request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version11 };
                        tryDowngrade = false;
                    }
                    else
                    {
                        throw new HttpSocketException(HttpSocketError.UnsupportedProtocol);
                    }

                    if (options._requestHeaders?.Count > 0) // use field to avoid lazily initializing the collection
                    {
                        foreach (string key in options.RequestHeaders)
                        {
                            request.Headers.TryAddWithoutValidation(key, options.RequestHeaders[key]);
                        }
                    }

                    string? secValue = AddHttpSocketHeaders(request, options);

                    // Issue the request.
                    CancellationTokenSource? linkedCancellation;
                    CancellationTokenSource externalAndAbortCancellation;
                    if (cancellationToken.CanBeCanceled) // avoid allocating linked source if external token is not cancelable
                    {
                        linkedCancellation =
                            externalAndAbortCancellation =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abortSource.Token);
                    }
                    else
                    {
                        linkedCancellation = null;
                        externalAndAbortCancellation = _abortSource;
                    }

                    using (linkedCancellation)
                    {
                        Task<HttpResponseMessage> sendTask = invoker is HttpClient client
                            ? client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, externalAndAbortCancellation.Token)
                            : invoker.SendAsync(request, externalAndAbortCancellation.Token);
                        response = await sendTask.ConfigureAwait(false);
                        externalAndAbortCancellation.Token.ThrowIfCancellationRequested(); // poll in case sends/receives in request/response didn't observe cancellation
                    }

                    ValidateResponse(response, secValue);
                    break;
                }
                catch (HttpRequestException ex) when
                    ((ex.HttpRequestError == HttpRequestError.ExtendedConnectNotSupported || ex.Data.Contains("HTTP2_ENABLED"))
                     && tryDowngrade
                     && (options.HttpVersion == HttpVersion.Version11 || options.HttpVersionPolicy == HttpVersionPolicy.RequestVersionOrLower))
                {
                }

            }

            // The SecHttpSocketProtocol header is optional.  We should only get it with a non-empty value if we requested subprotocols,
            // and then it must only be one of the ones we requested.  If we got a subprotocol other than one we requested (or if we
            // already got one in a previous header), fail. Otherwise, track which one we got.
            string? subprotocol = null;
            if (response.Headers.TryGetValues(HttpKnownHeaderNames.SecHttpSocketProtocol, out IEnumerable<string>? subprotocolEnumerableValues))
            {
                Debug.Assert(subprotocolEnumerableValues is string[]);
                string[] subprotocolArray = (string[])subprotocolEnumerableValues;
                if (subprotocolArray.Length > 0 && !string.IsNullOrEmpty(subprotocolArray[0]))
                {
                    if (options._requestedSubProtocols is not null)
                    {
                        foreach (string requestedProtocol in options._requestedSubProtocols)
                        {
                            if (requestedProtocol.Equals(subprotocolArray[0], StringComparison.OrdinalIgnoreCase))
                            {
                                subprotocol = requestedProtocol;
                                break;
                            }
                        }
                    }

                    if (subprotocol == null)
                    {
                        throw new HttpSocketException(
                            HttpSocketError.UnsupportedProtocol,
                            string.Format(Strings.net_HttpSockets_AcceptUnsupportedProtocol, string.Join(", ", options.RequestedSubProtocols), string.Join(", ", subprotocolArray)));
                    }
                }
            }

            //// Because deflate options are negotiated we need a new object
            //HttpSocketDeflateOptions? negotiatedDeflateOptions = null;
            //
            //if (options.DangerousDeflateOptions is not null && response.Headers.TryGetValues(HttpKnownHeaderNames.SecHttpSocketExtensions, out IEnumerable<string>? extensions))
            //{
            //    foreach (string extension in extensions)
            //    {
            //        if (extension.AsSpan().TrimStart().StartsWith(ClientHttpSocketDeflateConstants.Extension))
            //        {
            //            negotiatedDeflateOptions = ParseDeflateOptions(extension, options.DangerousDeflateOptions);
            //            break;
            //        }
            //    }
            //}

            // Get the response stream and wrap it in a web socket.
            Stream connectedStream = response.Content.ReadAsStream();
            Debug.Assert(connectedStream.CanWrite);
            Debug.Assert(connectedStream.CanRead);
            HttpSocket = HttpSocket.CreateFromStream(connectedStream, new HttpSocketCreationOptions
            {
                IsServer = false,
                SubProtocol = subprotocol,
                KeepAliveInterval = options.KeepAliveInterval,
                KeepAliveTimeout = options.KeepAliveTimeout,
                //DangerousDeflateOptions = negotiatedDeflateOptions
            });
            //_negotiatedDeflateOptions = negotiatedDeflateOptions;
        }
        catch (Exception exc)
        {
            if (_state < HttpSocketState.Closed)
            {
                _state = HttpSocketState.Closed;
            }

            Abort();
            disposeResponse = true;

            if (exc is HttpSocketException ||
                (exc is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                throw;
            }

            throw new HttpSocketException(HttpSocketError.Faulted, Strings.net_httpstatus_ConnectFailure, exc);
        }
        finally
        {
            if (response is not null)
            {
                if (options.CollectHttpResponseDetails)
                {
                    HttpStatusCode = response.StatusCode;
                    HttpResponseHeaders = new HttpResponseHeadersReadOnlyCollection(response.Headers);
                }

                if (disposeResponse)
                {
                    response.Dispose();
                }
            }

            // Disposing the invoker will not affect any active stream wrapped in the HttpSocket.
            if (disposeInvoker)
            {
                invoker?.Dispose();
            }
        }
    }

    private static HttpMessageInvoker SetupInvoker(ClientHttpSocketOptions options, out bool disposeInvoker)
    {
        // Create the invoker for this request and populate it with all of the options.
        // If the options are compatible, reuse a shared invoker.
        if (options.AreCompatibleWithCustomInvoker())
        {
            disposeInvoker = false;

            bool useDefaultProxy = options.Proxy is not null;

            ref HttpMessageInvoker? invokerRef = ref useDefaultProxy ? ref s_defaultInvokerDefaultProxy : ref s_defaultInvokerNoProxy;

            if (invokerRef is null)
            {
                var invoker = new HttpMessageInvoker(new SocketsHttpHandler()
                {
                    PooledConnectionLifetime = TimeSpan.Zero,
                    UseProxy = useDefaultProxy,
                    UseCookies = false,
                });

                if (Interlocked.CompareExchange(ref invokerRef, invoker, null) is not null)
                {
                    invoker.Dispose();
                }
            }

            return invokerRef;
        }
        else
        {
            disposeInvoker = true;
            var handler = new SocketsHttpHandler();
            handler.PooledConnectionLifetime = TimeSpan.Zero;
            handler.CookieContainer = options.Cookies;
            handler.UseCookies = options.Cookies != null;
            handler.SslOptions.RemoteCertificateValidationCallback = options.RemoteCertificateValidationCallback;

            handler.Credentials = options.UseDefaultCredentials ?
                CredentialCache.DefaultCredentials :
                options.Credentials;

            if (options.Proxy == null)
            {
                handler.UseProxy = false;
            }
            else if (options.Proxy != DefaultWebProxy.Instance)
            {
                handler.Proxy = options.Proxy;
            }

            if (options._clientCertificates?.Count > 0) // use field to avoid lazily initializing the collection
            {
                Debug.Assert(handler.SslOptions.ClientCertificates == null);
                handler.SslOptions.ClientCertificates = new X509Certificate2Collection();
                handler.SslOptions.ClientCertificates.AddRange(options.ClientCertificates);
            }

            return new HttpMessageInvoker(handler);
        }
    }
    
    /// <summary>Adds the necessary headers for the web socket request.</summary>
    /// <param name="request">The request to which the headers should be added.</param>
    /// <param name="options">The options controlling the request.</param>
    private static string? AddHttpSocketHeaders(HttpRequestMessage request, ClientHttpSocketOptions options)
    {
        // always exact because we handle downgrade here
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        string? secValue = null;

        if (request.Version == HttpVersion.Version11)
        {
            // Create the security key and expected response, then build all of the request headers
            KeyValuePair<string, string> secKeyAndSecHttpSocketAccept = CreateSecKeyAndSecHttpSocketAccept();
            secValue = secKeyAndSecHttpSocketAccept.Value;
            request.Headers.TryAddWithoutValidation(HttpKnownHeaderNames.Connection, HttpKnownHeaderNames.Upgrade);
            request.Headers.TryAddWithoutValidation(HttpKnownHeaderNames.Upgrade, "websocket");
            request.Headers.TryAddWithoutValidation(HttpKnownHeaderNames.SecHttpSocketKey, secKeyAndSecHttpSocketAccept.Key);
        }
        else if (request.Version == HttpVersion.Version20)
        {
            request.Headers.Protocol = "websocket";
        }

        request.Headers.TryAddWithoutValidation(HttpKnownHeaderNames.SecHttpSocketVersion, "13");

        if (options._requestedSubProtocols?.Count > 0)
        {
            request.Headers.TryAddWithoutValidation(HttpKnownHeaderNames.SecHttpSocketProtocol, string.Join(", ", options.RequestedSubProtocols));
        }
 
        return secValue;
    }

    private static void ValidateResponse(HttpResponseMessage response, string? secValue)
    {
        Debug.Assert(response.Version == HttpVersion.Version11 || response.Version == HttpVersion.Version20);

        if (response.Version == HttpVersion.Version11)
        {
            if (response.StatusCode != HttpStatusCode.SwitchingProtocols)
            {
                throw new HttpSocketException(HttpSocketError.NotAHttpSocket, string.Format(Strings.net_HttpSockets_ConnectStatusExpected, (int)response.StatusCode, (int)HttpStatusCode.SwitchingProtocols));
            }

            Debug.Assert(secValue != null);

            // The Connection, Upgrade, and SecHttpSocketAccept headers are required and with specific values.
            ValidateHeader(response.Headers, HttpKnownHeaderNames.Connection, "Upgrade");
            ValidateHeader(response.Headers, HttpKnownHeaderNames.Upgrade, "websocket");
            ValidateHeader(response.Headers, HttpKnownHeaderNames.SecHttpSocketAccept, secValue);
        }
        else if (response.Version == HttpVersion.Version20)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpSocketException(HttpSocketError.NotAHttpSocket, string.Format(Strings.net_HttpSockets_ConnectStatusExpected, (int)response.StatusCode, (int)HttpStatusCode.OK));
            }
        }

        if (response.Content is null)
        {
            throw new HttpSocketException(HttpSocketError.ConnectionClosedPrematurely);
        }
    }

    /// <summary>
    /// Creates a pair of a security key for sending in the Sec-HttpSocket-Key header and
    /// the associated response we expect to receive as the Sec-HttpSocket-Accept header value.
    /// </summary>
    /// <returns>A key-value pair of the request header security key and expected response header value.</returns>
    [SuppressMessage("Microsoft.Security", "CA5350", Justification = "Required by RFC6455")]
    private static KeyValuePair<string, string> CreateSecKeyAndSecHttpSocketAccept()
    {
        // GUID appended by the server as part of the security key response.  Defined in the RFC.
        ReadOnlySpan<byte> wsServerGuidBytes = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"u8;

        Span<byte> bytes = stackalloc byte[24 /* Base64 guid length */ + wsServerGuidBytes.Length];

        // Base64-encode a new Guid's bytes to get the security key
        bool success = Guid.NewGuid().TryWriteBytes(bytes);
        Debug.Assert(success);
        string secKey = Convert.ToBase64String(bytes.Slice(0, 16 /*sizeof(Guid)*/));

        // Get the corresponding ASCII bytes for seckey+wsServerGuidBytes
        int encodedSecKeyLength = Encoding.ASCII.GetBytes(secKey, bytes);
        wsServerGuidBytes.CopyTo(bytes.Slice(encodedSecKeyLength));

        // Hash the seckey+wsServerGuidBytes bytes
        SHA1.TryHashData(bytes, bytes, out int bytesWritten);
        Debug.Assert(bytesWritten == 20 /* SHA1 hash length */);

        // Return the security key + the base64 encoded hashed bytes
        return new KeyValuePair<string, string>(
            secKey,
            Convert.ToBase64String(bytes.Slice(0, bytesWritten)));
    }

    private static void ValidateHeader(HttpHeaders headers, string name, string expectedValue)
    {
        if (headers.NonValidated.TryGetValues(name, out HeaderStringValues hsv))
        {
            if (hsv.Count == 1)
            {
                foreach (string value in hsv)
                {
                    if (string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    break;
                }
            }

            throw new HttpSocketException(HttpSocketError.HeaderError, string.Format(Strings.net_HttpSockets_InvalidResponseHeader, name, hsv));
        }

        throw new HttpSocketException(HttpSocketError.Faulted, string.Format(Strings.net_HttpSockets_MissingResponseHeader, name));
    }

    /// <summary>Used as a sentinel to indicate that ClientHttpSocket should use the system's default proxy.</summary>
    internal sealed class DefaultWebProxy : IWebProxy
    {
        public static DefaultWebProxy Instance { get; } = new DefaultWebProxy();
        public ICredentials? Credentials { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public Uri? GetProxy(Uri destination) => throw new NotSupportedException();
        public bool IsBypassed(Uri host) => throw new NotSupportedException();
    }
}
#endif