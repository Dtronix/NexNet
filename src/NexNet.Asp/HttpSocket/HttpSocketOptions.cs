using System.Collections.Generic;

namespace NexNet.Asp.HttpSocket;

/// <summary>
/// Options that control how a <seealso cref="HttpSocket"/> is created.
/// </summary>
public sealed class HttpSocketOptions
{
    /// <summary>
    /// Set the Origin header values allowed for HttpSocket upgrade requests, to prevent cross-site hijacking of
    /// the connection. Empty (the default) or a "*" entry allows every origin; the comparison ignores case.
    /// </summary>
    public List<string> AllowedOrigins { get; } = new List<string>();
}
