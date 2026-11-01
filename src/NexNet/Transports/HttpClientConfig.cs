using System;
using System.Net.Http.Headers;
using System.Net.Security;

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

    /// <summary>
    /// TLS options for <c>https</c>/<c>wss</c> URLs: client certificates, server certificate validation, protocols
    /// and revocation checking. Null uses the platform defaults (no client certificate, system chain validation).
    /// </summary>
    /// <remarks>
    /// <see cref="SslClientAuthenticationOptions.TargetHost"/> defaults to the URL host when not set. The
    /// <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/> sender is the HTTP request,
    /// not an <see cref="SslStream"/>. Ignored for <c>http</c>/<c>ws</c> URLs.
    /// </remarks>
    public SslClientAuthenticationOptions? SslClientAuthenticationOptions { get; set; }
}
