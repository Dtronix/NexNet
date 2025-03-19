// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace NexNet.Transports.HttpSocket.Internals;

public enum HttpSocketMessageType
{
    Text = 0,
    Binary = 1,
    Close = 2
}