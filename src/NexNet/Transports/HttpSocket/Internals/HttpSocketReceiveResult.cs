// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace NexNet.Transports.HttpSocket.Internals;

public class HttpSocketReceiveResult
{
    public HttpSocketReceiveResult(int count, HttpSocketMessageType messageType, bool endOfMessage)
        : this(count, messageType, endOfMessage, null, null)
    {
    }

    public HttpSocketReceiveResult(int count,
        HttpSocketMessageType messageType,
        bool endOfMessage,
        HttpSocketCloseStatus? closeStatus,
        string? closeStatusDescription)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Count = count;
        EndOfMessage = endOfMessage;
        MessageType = messageType;
        CloseStatus = closeStatus;
        CloseStatusDescription = closeStatusDescription;
    }

    public int Count { get; }
    public bool EndOfMessage { get; }
    public HttpSocketMessageType MessageType { get; }
    public HttpSocketCloseStatus? CloseStatus { get; }
    public string? CloseStatusDescription { get; }
}