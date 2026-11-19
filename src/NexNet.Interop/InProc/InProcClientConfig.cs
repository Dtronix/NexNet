using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NexNet.InteropServer;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.Uds;
using NexNet.Transports.WebSocket;

namespace NexNet.Interop.InProc;

/// <summary>Maps a <see cref="ClientSpec"/> to the configuration of the C# client.</summary>
public static class InProcClientConfig
{
    /// <summary>Creates the C# client configuration of <paramref name="spec"/> over <paramref name="transport"/>.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="fakeTime">The test clock when <see cref="ClientSpec.FakeTime"/> is set; otherwise null.</param>
    public static ClientConfig Create(ClientSpec spec, ClientTransport transport, out FakeTimeProvider? fakeTime)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var address = spec.Address;
        ClientConfig config = transport switch
        {
            ClientTransport.Tcp => new TcpClientConfig { EndPoint = ParseEndPoint(address, "tcp://") },
            ClientTransport.TcpTls => new TcpTlsClientConfig
            {
                EndPoint = ParseEndPoint(address, "tls://"),
                SslClientAuthenticationOptions = CreateTls(spec, spec.TlsServerName ?? "localhost"),
            },
            ClientTransport.Uds => new UdsClientConfig
            {
                EndPoint = new UnixDomainSocketEndPoint(address.StartsWith("uds:", StringComparison.Ordinal) ? address[4..] : address),
            },
            ClientTransport.HttpSocket or ClientTransport.HttpSocketTls => new HttpSocketClientConfig
            {
                Url = new Uri(address),
                SslClientAuthenticationOptions = transport.IsSecure() ? CreateTls(spec, null) : null,
            },
            ClientTransport.WebSocket or ClientTransport.WebSocketTls => new WebSocketClientConfig
            {
                Url = new Uri(address),
                SslClientAuthenticationOptions = transport.IsSecure() ? CreateTls(spec, null) : null,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(transport), transport, null),
        };

        if (spec.AuthorizationHeader != null)
        {
            if (config is not HttpClientConfig http)
                throw new NotSupportedException($"An Authorization header needs an HTTP transport, not {transport}.");
            http.AuthenticationHeader = AuthenticationHeaderValue.Parse(spec.AuthorizationHeader);
        }

        if (spec.AuthToken is { } authToken)
            config.Authenticate = () => Encoding.UTF8.GetBytes(authToken);
        if (spec.ConnectionTimeoutMs is { } connectionTimeout)
            config.ConnectionTimeout = connectionTimeout;
        if (spec.TimeoutMs is { } timeout)
            config.Timeout = timeout;
        if (spec.HandshakeTimeoutMs is { } handshakeTimeout)
            config.HandshakeTimeout = handshakeTimeout;
        if (spec.PingIntervalMs is { } pingInterval)
            config.PingInterval = pingInterval;
        if (spec.MaxMessageSize is { } maxMessageSize)
            config.MaxMessageSize = maxMessageSize;
        if (spec.InvocationCredits is { } invocationCredits)
            config.InvocationCredits = invocationCredits;
        if (spec.MaxConcurrentConnectionInvocations is { } maxConcurrent)
            config.MaxConcurrentConnectionInvocations = maxConcurrent;
        if (spec.RemoteExceptionDetail is { } remoteExceptionDetail)
            config.RemoteExceptionDetail = remoteExceptionDetail;
        if (spec.ReconnectDelaysMs is { } delays)
            config.ReconnectionPolicy = new DefaultReconnectionPolicy(
                delays.Select(d => TimeSpan.FromMilliseconds(d)).ToArray(), spec.ReconnectContinuously);

        // LogLevel is for language hosts; the in-process logger filters itself.
        if (spec.Logger is { } logger)
            config.Logger = logger;

        fakeTime = null;
        if (spec.FakeTime)
        {
            fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
            config.Time = fakeTime;
        }

        return config;
    }

    private static IPEndPoint ParseEndPoint(string address, string scheme)
        => IPEndPoint.Parse(address.StartsWith(scheme, StringComparison.Ordinal) ? address[scheme.Length..] : address);

    private static SslClientAuthenticationOptions CreateTls(ClientSpec spec, string? targetHost)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            AllowRenegotiation = false,
        };

        if (spec.InsecureSkipVerify)
        {
            options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (spec.PinSha256 is { } pin)
        {
            options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate == null)
                    return false;
                using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                var spki = SHA256.HashData(leaf.PublicKey.ExportSubjectPublicKeyInfo());
                return string.Equals(Convert.ToBase64String(spki), pin, StringComparison.Ordinal);
            };
        }
        else if (spec.CaPem is { } caPem)
        {
            options.RemoteCertificateValidationCallback =
                InteropCertificates.CreateValidationCallback(X509Certificate2.CreateFromPem(caPem));
        }

        if (spec.ClientCertificatePem is { } certificatePem)
        {
            if (spec.ClientKeyPem == null)
                throw new ArgumentException("ClientCertificatePem needs ClientKeyPem.", nameof(spec));

            // Re-imported from PKCS#12: SChannel cannot use the ephemeral key of a PEM import.
            using var ephemeral = X509Certificate2.CreateFromPem(certificatePem, spec.ClientKeyPem);
            var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
            options.ClientCertificates = new X509CertificateCollection { certificate };
        }

        return options;
    }
}
