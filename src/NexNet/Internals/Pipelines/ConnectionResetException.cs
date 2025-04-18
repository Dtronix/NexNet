﻿#nullable disable
using System;
using System.IO;

namespace NexNet.Internals.Pipelines;

/// <summary>
/// Indicates that a connection was reset
/// </summary>
[Serializable]
internal sealed class ConnectionResetException : IOException
{
    /// <summary>
    /// Create a new ConnectionResetException instance
    /// </summary>
    public ConnectionResetException() : this("The connection was reset") { }

    /// <summary>
    /// Create a new ConnectionResetException instance
    /// </summary>
    public ConnectionResetException(string message) : base(message) { }
    /// <summary>
    /// Create a new ConnectionResetException instance
    /// </summary>
    public ConnectionResetException(string message, Exception inner) : base(message, inner) { }
}
