using System;
using System.Net.Http.Headers;

namespace NexNet.Transports;

/// <summary>
/// Base configurations for all Http based connections
/// </summary>
public abstract class HttpClientConfig : ClientConfig
{
    /// <summary>
    /// Endpoint
    /// </summary>
    public required Uri Url { get; set; }

    /// <summary>
    /// Authentication header if used. Null if not.
    /// </summary>
    public AuthenticationHeaderValue? AuthenticationHeader { get; set; }
}
