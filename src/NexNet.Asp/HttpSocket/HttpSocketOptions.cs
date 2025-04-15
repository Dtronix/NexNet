using System.Collections.Generic;

namespace NexNet.Asp.HttpSocket;

/// <summary>
/// Options that control how a <seealso cref="HttpSocket"/> is created.
/// </summary>
public sealed class HttpSocketOptions
{
    /// <summary>
    /// Set the Origin header values allowed for WebSocket requests to prevent Cross-Site WebSocket Hijacking.
    /// By default all Origins are allowed.
    /// </summary>
    public List<string> AllowedOrigins { get; } = new List<string>();
}
