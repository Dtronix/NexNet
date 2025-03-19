// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;

namespace NexNet.Transports.HttpSocket.Internals;

/// <summary>
/// Options that control how a <seealso cref="HttpSocket"/> is created.
/// </summary>
public sealed class HttpSocketCreationOptions
{
    private string? _subProtocol;
    private TimeSpan _keepAliveInterval = HttpSocketDefaults.DefaultKeepAliveInterval;
    private TimeSpan _keepAliveTimeout = HttpSocketDefaults.DefaultKeepAliveTimeout;

    /// <summary>
    /// Defines if this websocket is the server-side of the connection. The default value is false.
    /// </summary>
    public bool IsServer { get; set; }

    /// <summary>
    /// The agreed upon sub-protocol that was used when creating the connection.
    /// </summary>
    public string? SubProtocol
    {
        get => _subProtocol;
        set
        {
            if (value is not null)
            {
                HttpSocketValidate.ValidateSubprotocol(value);
            }
            _subProtocol = value;
        }
    }

    /// <summary>
    /// The keep-alive interval to use, or <see cref="TimeSpan.Zero"/> or <see cref="Timeout.InfiniteTimeSpan"/> to disable keep-alives.
    /// If <see cref="HttpSocketCreationOptions.KeepAliveTimeout"/> is set, then PING messages are sent and peer's PONG responses are expected, otherwise,
    /// unsolicited PONG messages are used as a keep-alive heartbeat.
    /// The default is <see cref="TimeSpan.Zero"/>.
    /// </summary>
    public TimeSpan KeepAliveInterval
    {
        get => _keepAliveInterval;
        set
        {
            if (value != Timeout.InfiniteTimeSpan && value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(KeepAliveInterval), value,
                    string.Format(Strings.net_HttpSockets_ArgumentOutOfRange_TooSmall, 0));
            }
            _keepAliveInterval = value;
        }
    }

    /// <summary>
    /// The timeout to use when waiting for the peer's PONG in response to us sending a PING; or <see cref="TimeSpan.Zero"/> or
    /// <see cref="Timeout.InfiniteTimeSpan"/> to disable waiting for peer's response, and use an unsolicited PONG as a Keep-Alive heartbeat instead.
    /// The default is <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public TimeSpan KeepAliveTimeout
    {
        get => _keepAliveTimeout;
        set
        {
            if (value != Timeout.InfiniteTimeSpan && value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(KeepAliveTimeout), value,
                    string.Format(Strings.net_HttpSockets_ArgumentOutOfRange_TooSmall, 0));
            }
            _keepAliveTimeout = value;
        }
    }
}