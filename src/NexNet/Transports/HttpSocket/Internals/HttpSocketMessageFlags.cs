// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace NexNet.Transports.HttpSocket.Internals;

/// <summary>
/// Flags for controlling how the <see cref="HttpSocket"/> should send a message.
/// </summary>
[Flags]
public enum HttpSocketMessageFlags
{
    /// <summary>
    /// None
    /// </summary>
    None = 0,

    /// <summary>
    /// Indicates that the data in "buffer" is the last part of a message.
    /// </summary>
    EndOfMessage = 1,

    /// <summary>
    /// Disables compression for the message if compression has been enabled for the <see cref="HttpSocket"/> instance.
    /// </summary>
    DisableCompression = 2
}