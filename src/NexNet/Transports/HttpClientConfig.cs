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
    /// <see cref="SslClientAuthenticationOptions.TargetHost"/> is ignored: the HTTP handler always uses the URL host
    /// as the SNI name and validates the server certificate against it. To validate against another name, connect
    /// with a URL that uses that name. The
    /// <see cref="SslClientAuthenticationOptions.RemoteCertificateValidationCallback"/> sender is the HTTP request,
    /// not an <see cref="SslStream"/>. Ignored for <c>http</c>/<c>ws</c> URLs.
    /// </remarks>
    public SslClientAuthenticationOptions? SslClientAuthenticationOptions { get; set; }

    /// <summary>
    /// True for <c>https</c> and <c>wss</c> URLs; false for <c>http</c> and <c>ws</c>.
    /// </summary>
    protected internal override bool IsSecureTransport
        => Url != null
           && (string.Equals(Url.Scheme, "https", StringComparison.OrdinalIgnoreCase)
               || string.Equals(Url.Scheme, "wss", StringComparison.OrdinalIgnoreCase));
}
