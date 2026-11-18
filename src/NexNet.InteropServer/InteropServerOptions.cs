using NexNet.Logging;
using NexNet.Transports;

namespace NexNet.InteropServer;

/// <summary>
/// Options of one interop server (one <c>NexusServer</c> with every <see cref="InteropEndpointKind"/> endpoint).
/// </summary>
public sealed class InteropServerOptions
{
    /// <summary>
    /// Shared-host routing key. Null (the default) serves the HttpSocket and WebSocket endpoints at the root paths
    /// (<c>/hs</c>, <c>/hss</c>, <c>/ws</c>, <c>/wss</c>); otherwise at <c>/t/{TestId}/…</c>. Letters, digits,
    /// <c>-</c>, <c>_</c> and <c>.</c>; at most 64 characters. Unique within a host.
    /// </summary>
    public string? TestId { get; set; }

    /// <summary>
    /// Validates client greeting tokens. When set, every endpoint requires authentication
    /// (<c>ServerConfig.Authenticate</c>) and nexuses implementing <see cref="IInteropTokenValidatingNexus"/> receive
    /// this validator. Null (the default) accepts every client without authentication.
    /// </summary>
    public InteropTokenValidator? TokenValidator { get; set; }

    /// <summary>
    /// Session timeout in milliseconds (<c>ConfigBase.Timeout</c>) of every endpoint without an entry in
    /// <see cref="EndpointTimeouts"/>. Null keeps the NexNet default.
    /// </summary>
    public int? Timeout { get; set; }

    /// <summary>
    /// Per-endpoint session timeouts in milliseconds; they override <see cref="Timeout"/>.
    /// </summary>
    public IDictionary<InteropEndpointKind, int> EndpointTimeouts { get; } = new Dictionary<InteropEndpointKind, int>();

    /// <summary>
    /// Origin header values allowed on the WebSocket and HttpSocket endpoints. Empty (the default) or a <c>*</c>
    /// entry allows every origin; use <c>*</c> for the browser host.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = new List<string>();

    /// <summary>
    /// Logger for every endpoint's configuration. Null (the default) disables NexNet logging.
    /// </summary>
    public INexusLogger? Logger { get; set; }

    /// <summary>
    /// Called with each endpoint's configuration after the options above are applied and before the endpoint is
    /// added, to change any other setting (message size, flow control, ...). The TCP endpoint is the primary
    /// configuration, so server-wide settings (rate limiting, meter factory) belong on it.
    /// </summary>
    public Action<InteropEndpointKind, ServerConfig>? ConfigureEndpoint { get; set; }

    /// <summary>
    /// Directory to create the UDS socket folder in. Null (the default) uses the system temp directory. Keep it short:
    /// socket paths are limited to about 100 characters.
    /// </summary>
    public string? UdsDirectory { get; set; }
}

/// <summary>
/// Options of an <see cref="InteropServerHost"/> (the Kestrel listeners shared by its servers).
/// </summary>
public sealed class InteropHostOptions
{
    /// <summary>
    /// Certificate for HTTPS/WSS and for the TCP TLS listeners. Null (the default) loads the F08 test server
    /// certificate (<see cref="InteropCertificates.LoadServerCertificate"/>).
    /// </summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? ServerCertificate { get; set; }
}
