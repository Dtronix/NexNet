// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace NexNet.Transports.HttpSocket.Internals;

public sealed class ClientHttpSocketOptions
{
    private bool _isReadOnly; // After ConnectAsync is called the options cannot be modified.
    private TimeSpan _keepAliveInterval = HttpSocketDefaults.DefaultClientKeepAliveInterval;
    private TimeSpan _keepAliveTimeout = HttpSocketDefaults.DefaultKeepAliveTimeout;
    private bool _useDefaultCredentials;
    private ICredentials? _credentials;
    private IWebProxy? _proxy;
    private CookieContainer? _cookies;
    private int _receiveBufferSize = 0x1000;
    private ArraySegment<byte>? _buffer;
    private RemoteCertificateValidationCallback? _remoteCertificateValidationCallback;

    internal X509CertificateCollection? _clientCertificates;
    internal WebHeaderCollection? _requestHeaders;
    internal List<string>? _requestedSubProtocols;
    private Version _version = new Version(1, 1);
    private HttpVersionPolicy _versionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    private bool _collectHttpResponseDetails;

    internal bool AreCompatibleWithCustomInvoker() =>
        !UseDefaultCredentials &&
        Credentials is null &&
        (_clientCertificates?.Count ?? 0) == 0 &&
        RemoteCertificateValidationCallback is null &&
        Cookies is null &&
        (Proxy is null || Proxy == HttpSocketHandle.DefaultWebProxy.Instance);

    internal ClientHttpSocketOptions() { } // prevent external instantiation

    /// <summary>Gets or sets the HTTP version to use.</summary>
    /// <value>The HTTP message version. The default value is <c>1.1</c>.</value>
    public Version HttpVersion
    {
        get => _version;
        [UnsupportedOSPlatform("browser")]
        set
        {
            ThrowIfReadOnly();
            ArgumentNullException.ThrowIfNull(value);
            _version = value;
        }
    }

    /// <summary>Gets or sets the policy that determines how <see cref="ClientHttpSocketOptions.HttpVersion" /> is interpreted and how the final HTTP version is negotiated with the server.</summary>
    /// <value>The version policy used when the HTTP connection is established.</value>
    public HttpVersionPolicy HttpVersionPolicy
    {
        get => _versionPolicy;
        [UnsupportedOSPlatform("browser")]
        set
        {
            ThrowIfReadOnly();
            _versionPolicy = value;
        }
    }

    [UnsupportedOSPlatform("browser")]
    // Note that some headers are restricted like Host.
    public void SetRequestHeader(string headerName, string? headerValue)
    {
        ThrowIfReadOnly();

        // WebHeaderCollection performs validation of headerName/headerValue.
        RequestHeaders.Set(headerName, headerValue);
    }

    internal WebHeaderCollection RequestHeaders => _requestHeaders ??= new WebHeaderCollection();

    internal List<string> RequestedSubProtocols => _requestedSubProtocols ??= new List<string>();

    [UnsupportedOSPlatform("browser")]
    public bool UseDefaultCredentials
    {
        get => _useDefaultCredentials;
        set
        {
            ThrowIfReadOnly();
            _useDefaultCredentials = value;
        }
    }

    [UnsupportedOSPlatform("browser")]
    public ICredentials? Credentials
    {
        get => _credentials;
        set
        {
            ThrowIfReadOnly();
            _credentials = value;
        }
    }

    [UnsupportedOSPlatform("browser")]
    public IWebProxy? Proxy
    {
        get => _proxy;
        set
        {
            ThrowIfReadOnly();
            _proxy = value;
        }
    }

    [UnsupportedOSPlatform("browser")]
    public X509CertificateCollection ClientCertificates
    {
        get => _clientCertificates ??= new X509CertificateCollection();
        set
        {
            ThrowIfReadOnly();
            ArgumentNullException.ThrowIfNull(value);
            _clientCertificates = value;
        }
    }

    [UnsupportedOSPlatform("browser")]
    public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback
    {
        get => _remoteCertificateValidationCallback;
        set
        {
            ThrowIfReadOnly();
            _remoteCertificateValidationCallback = value;
        }
    }

    [UnsupportedOSPlatform("browser")]
    public CookieContainer? Cookies
    {
        get => _cookies;
        set
        {
            ThrowIfReadOnly();
            _cookies = value;
        }
    }

    public void AddSubProtocol(string subProtocol)
    {
        ThrowIfReadOnly();
        HttpSocketValidate.ValidateSubprotocol(subProtocol);

        // Duplicates not allowed.
        List<string> subprotocols = RequestedSubProtocols; // force initialization of the list
        foreach (string item in subprotocols)
        {
            if (string.Equals(item, subProtocol, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format(Strings.net_HttpSockets_NoDuplicateProtocol, subProtocol), nameof(subProtocol));
            }
        }
        subprotocols.Add(subProtocol);
    }

    /// <summary>
    /// The keep-alive interval to use, or <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/> to disable keep-alives.
    /// If <see cref="ClientHttpSocketOptions.KeepAliveTimeout"/> is set, then PING messages are sent and peer's PONG responses are expected, otherwise,
    /// unsolicited PONG messages are used as a keep-alive heartbeat.
    /// The default is <see cref="HttpSocket.DefaultKeepAliveInterval"/> (typically 30 seconds).
    /// </summary>
    [UnsupportedOSPlatform("browser")]
    public TimeSpan KeepAliveInterval
    {
        get => _keepAliveInterval;
        set
        {
            ThrowIfReadOnly();
            if (value != Timeout.InfiniteTimeSpan && value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    string.Format(Strings.net_HttpSockets_ArgumentOutOfRange_TooSmall,
                        Timeout.InfiniteTimeSpan.ToString()));
            }
            _keepAliveInterval = value;
        }
    }

    /// <summary>
    /// The timeout to use when waiting for the peer's PONG in response to us sending a PING; or <see cref="TimeSpan.Zero"/> or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to disable waiting for peer's response, and use an unsolicited PONG as a Keep-Alive heartbeat instead.
    /// The default is <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    [UnsupportedOSPlatform("browser")]
    public TimeSpan KeepAliveTimeout
    {
        get => _keepAliveTimeout;
        set
        {
            ThrowIfReadOnly();
            if (value != Timeout.InfiniteTimeSpan && value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    string.Format(Strings.net_HttpSockets_ArgumentOutOfRange_TooSmall,
                        Timeout.InfiniteTimeSpan.ToString()));
            }
            _keepAliveTimeout = value;
        }
    }

    internal int ReceiveBufferSize => _receiveBufferSize;
    internal ArraySegment<byte>? Buffer => _buffer;

    [UnsupportedOSPlatform("browser")]
    public void SetBuffer(int receiveBufferSize, int sendBufferSize)
    {
        ThrowIfReadOnly();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBufferSize);

        _receiveBufferSize = receiveBufferSize;
        _buffer = null;
    }

    [UnsupportedOSPlatform("browser")]
    public void SetBuffer(int receiveBufferSize, int sendBufferSize, ArraySegment<byte> buffer)
    {
        ThrowIfReadOnly();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveBufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sendBufferSize);

        HttpSocketValidate.ValidateArraySegment(buffer, nameof(buffer));
        ArgumentOutOfRangeException.ThrowIfZero(buffer.Count, nameof(buffer));

        _receiveBufferSize = receiveBufferSize;
        _buffer = buffer;
    }

    /// <summary>
    /// Indicates whether <see cref="ClientHttpSocket.HttpStatusCode" /> and <see cref="ClientHttpSocket.HttpResponseHeaders" /> should be set when establishing the connection.
    /// </summary>
    [UnsupportedOSPlatform("browser")]
    public bool CollectHttpResponseDetails
    {
        get => _collectHttpResponseDetails;
        set
        {
            ThrowIfReadOnly();
            _collectHttpResponseDetails = value;
        }
    }

    internal void SetToReadOnly()
    {
        Debug.Assert(!_isReadOnly, "Already set");
        _isReadOnly = true;
    }

    private void ThrowIfReadOnly()
    {
        if (_isReadOnly)
        {
            throw new InvalidOperationException(Strings.net_HttpSockets_AlreadyStarted);
        }
    }
}