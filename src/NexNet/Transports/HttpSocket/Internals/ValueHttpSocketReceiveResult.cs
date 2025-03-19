// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;

namespace NexNet.Transports.HttpSocket.Internals;

/// <summary>Represents the result of performing a single <see cref="HttpSocket.ReceiveAsync(Memory{T}, System.Threading.CancellationToken)"/> operation on a <see cref="HttpSocket"/>.</summary>
public readonly struct ValueHttpSocketReceiveResult
{
    private readonly uint _countAndEndOfMessage;
    private readonly HttpSocketMessageType _messageType;

    /// <summary>Creates an instance of the <see cref="ValueHttpSocketReceiveResult"/> value type.</summary>
    /// <param name="count">The number of bytes received.</param>
    /// <param name="messageType">The type of message that was received.</param>
    /// <param name="endOfMessage">Whether this is the final message.</param>
    public ValueHttpSocketReceiveResult(int count, HttpSocketMessageType messageType, bool endOfMessage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if ((uint)messageType > (uint)HttpSocketMessageType.Close) ThrowMessageTypeOutOfRange();

        _countAndEndOfMessage = (uint)count | (uint)(endOfMessage ? 1 << 31 : 0);
        _messageType = messageType;

        Debug.Assert(count == Count);
        Debug.Assert(messageType == MessageType);
        Debug.Assert(endOfMessage == EndOfMessage);
    }

    public int Count => (int)(_countAndEndOfMessage & 0x7FFFFFFF);
    public bool EndOfMessage => (_countAndEndOfMessage & 0x80000000) == 0x80000000;
    public HttpSocketMessageType MessageType => _messageType;

    private static void ThrowMessageTypeOutOfRange() => throw new ArgumentOutOfRangeException("messageType");
}