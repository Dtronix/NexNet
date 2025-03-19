// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace NexNet.Transports.HttpSocket.Internals;

public enum HttpSocketState
{
    None = 0,
    Connecting = 1,
    Open = 2,
    CloseSent = 3, // HttpSocket close handshake started form local endpoint
    CloseReceived = 4, // HttpSocket close message received from remote endpoint. Waiting for app to call close
    Closed = 5,
    Aborted = 6,
}