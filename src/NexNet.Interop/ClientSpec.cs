using System.Text.Json.Serialization;
using NexNet.InteropServer;
using NexNet.Invocation;
using NexNet.Logging;
using NexNet.Transports;

namespace NexNet.Interop;

/// <summary>
/// How to configure a client under test, in every language. Each driver maps it to its native configuration; an option
/// a language cannot honor fails the client creation visibly, never silently.
/// </summary>
/// <remarks>
/// Every member is plain data (strings, numbers, flags), so a remote driver can send the spec to a language host as
/// JSON. Null means the client's default. <see cref="Logger"/> is in-process only.
/// </remarks>
public sealed class ClientSpec
{
    /// <summary>The transport; null takes the one of the <see cref="ClientTarget"/>.</summary>
    public ClientTransport? Transport { get; set; }

    /// <summary>
    /// Where to connect, as <see cref="InteropEndpoints.GetAddress"/> writes it: <c>tcp://host:port</c>,
    /// <c>tls://host:port</c>, <c>uds:</c> followed by the socket path, the <c>http(s)://</c> / <c>ws(s)://</c> URL, or
    /// <c>quic://host:port</c>.
    /// </summary>
    public string Address { get; set; } = "";

    /// <summary>TLS server name for TLS over TCP and QUIC; null uses <c>localhost</c>. HTTP transports use the URL host.</summary>
    public string? TlsServerName { get; set; }

    /// <summary>PEM of the only root CA the client trusts (<see cref="ClientCapabilities.CustomCa"/>); null for the system store.</summary>
    public string? CaPem { get; set; }

    /// <summary>PEM certificate the client presents (<see cref="ClientCapabilities.ClientCertificate"/>).</summary>
    public string? ClientCertificatePem { get; set; }

    /// <summary>PEM private key of <see cref="ClientCertificatePem"/>.</summary>
    public string? ClientKeyPem { get; set; }

    /// <summary>
    /// Base64 SHA-256 of the server's SubjectPublicKeyInfo (<see cref="ClientCapabilities.Pinning"/>). When set, the pin
    /// alone decides trust.
    /// </summary>
    public string? PinSha256 { get; set; }

    /// <summary>Accepts any server certificate. Test-only.</summary>
    public bool InsecureSkipVerify { get; set; }

    /// <summary>Connection timeout in milliseconds.</summary>
    public int? ConnectionTimeoutMs { get; set; }

    /// <summary>Session idle timeout in milliseconds.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>Handshake timeout in milliseconds.</summary>
    public int? HandshakeTimeoutMs { get; set; }

    /// <summary>Ping interval in milliseconds; null derives it from the server greeting.</summary>
    public int? PingIntervalMs { get; set; }

    /// <summary>Reconnect delays in milliseconds; null disables reconnection.</summary>
    public int[]? ReconnectDelaysMs { get; set; }

    /// <summary>Keeps retrying with the last delay once <see cref="ReconnectDelaysMs"/> is used up.</summary>
    public bool ReconnectContinuously { get; set; }

    /// <summary>The NexNet greeting token (<c>Authenticate</c>), as UTF-8 text.</summary>
    public string? AuthToken { get; set; }

    /// <summary>
    /// HTTP <c>Authorization</c> header value, e.g. <c>Bearer abc</c> (HttpSocket/WebSocket only;
    /// <see cref="ClientCapabilities.AuthorizationHeader"/>).
    /// </summary>
    public string? AuthorizationHeader { get; set; }

    /// <summary>Largest message the client accepts, in bytes.</summary>
    public int? MaxMessageSize { get; set; }

    /// <summary>Invocation credits the client grants the server.</summary>
    public int? InvocationCredits { get; set; }

    /// <summary>
    /// How many invocations from the server the client runs at once (<c>MaxConcurrentConnectionInvocations</c>, 1 – 1000);
    /// further ones queue.
    /// </summary>
    public int? MaxConcurrentConnectionInvocations { get; set; }

    /// <summary>
    /// What the client sends the server when a client nexus method it runs fails (<c>RemoteExceptionDetail</c>); null is
    /// the client's default (<see cref="NexNet.Transports.RemoteExceptionDetail.None"/>).
    /// </summary>
    public RemoteExceptionDetail? RemoteExceptionDetail { get; set; }

    /// <summary>
    /// The application error the client nexus's <c>OnInvocationException</c> hook returns for every failed client nexus
    /// method, whatever <see cref="RemoteExceptionDetail"/> says; null leaves the hook unset (it returns null).
    /// </summary>
    public NexusRemoteError? OnInvocationException { get; set; }

    /// <summary>Runs the client's timers on a test clock moved by <see cref="IClientUnderTest.AdvanceTimeAsync"/> (<see cref="ClientCapabilities.FakeTime"/>).</summary>
    public bool FakeTime { get; set; }

    /// <summary>Client log level; null disables client logging.</summary>
    public NexusLogLevel? LogLevel { get; set; }

    /// <summary>In-process only: the logger of the C# client. Not sent to language hosts.</summary>
    [JsonIgnore]
    public INexusLogger? Logger { get; set; }

    /// <summary>
    /// A spec that connects to <paramref name="transport"/>'s endpoint of an interop server and trusts the F08 test CA.
    /// </summary>
    public static ClientSpec ForEndpoint(InteropEndpoints endpoints, ClientTransport transport)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return new ClientSpec
        {
            Transport = transport,
            Address = endpoints.GetAddress(transport.ToEndpointKind()),
            CaPem = transport.IsSecure() ? File.ReadAllText(InteropCertificates.TestCaPemPath) : null,
        };
    }

    /// <summary>A shallow copy.</summary>
    public ClientSpec Clone()
    {
        var copy = (ClientSpec)MemberwiseClone();
        copy.ReconnectDelaysMs = ReconnectDelaysMs?.ToArray();
        return copy;
    }
}
