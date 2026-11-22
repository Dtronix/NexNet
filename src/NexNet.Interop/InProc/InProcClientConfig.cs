using System.Globalization;
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
using NexNet.Quic;
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
        if (spec.QueryToken is { } queryToken)
        {
            if (transport is not (ClientTransport.HttpSocket or ClientTransport.HttpSocketTls or ClientTransport.WebSocket or ClientTransport.WebSocketTls))
                throw new NotSupportedException($"A query-string token needs an HTTP transport, not {transport}.");
            address = WithQueryToken(address, queryToken);
        }

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
            ClientTransport.Quic => new QuicClientConfig
            {
                EndPoint = ParseEndPoint(address, "quic://"),
                // msquic needs a DNS server name (SNI) for the handshake.
                SslClientAuthenticationOptions = CreateTls(spec, spec.TlsServerName ?? "localhost"),
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
        if (spec.Authenticate is { } authenticate)
        {
            if (spec.AuthToken != null)
                throw new ArgumentException("AuthToken and Authenticate are exclusive.", nameof(spec));
            config.AuthenticateAsync = CreateAuthenticate(authenticate);
        }

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
        if (spec.MaxSeparatePoolInvocations is { } maxSeparate)
            config.MaxSeparatePoolInvocations = maxSeparate;
        if (spec.InvocationQueueWarningThresholdMs is { } queueWarning)
            config.InvocationQueueWarningThreshold = queueWarning;
        if (spec.SessionBudget is { } sessionBudget)
            config.SessionBudget = sessionBudget;
        if (spec.MaxPipes is { } maxPipes)
            config.MaxPipes = maxPipes;
        if (spec.PipeWindow is { } pipeWindow)
            config.PipeWindow = pipeWindow;
        if (spec.ProgressTimeoutMs is { } progressTimeout)
            config.ProgressTimeout = progressTimeout;
        if (spec.RemoteExceptionDetail is { } remoteExceptionDetail)
            config.RemoteExceptionDetail = remoteExceptionDetail;
        if (spec.ReconnectDelaysMs is { } delays)
        {
            var timeSpans = delays.Select(d => TimeSpan.FromMilliseconds(d)).ToArray();
            config.ReconnectionPolicy = spec.ReconnectReasons is { } reasons
                ? new DefaultReconnectionPolicy(timeSpans, spec.ReconnectContinuously) { ReconnectReasons = reasons.ToHashSet() }
                : new DefaultReconnectionPolicy(timeSpans, spec.ReconnectContinuously);
        }
        else if (spec.ReconnectReasons != null)
        {
            throw new ArgumentException("ReconnectReasons needs ReconnectDelaysMs.", nameof(spec));
        }

        // Without a LogLevel the in-process logger gets every line; with one, the lines at or above it.
        if (spec.Logger is { } logger && spec.LogLevel != NexNet.Logging.NexusLogLevel.None)
            config.Logger = spec.LogLevel is { } level ? new LevelFilterLogger(logger, level) : logger;

        fakeTime = null;
        if (spec.FakeTime)
        {
            fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
            config.Time = fakeTime;
        }

        return config;
    }

    /// <summary>The scripted authenticate callback (<see cref="AuthenticateScript"/>).</summary>
    private static Func<CancellationToken, ValueTask<Memory<byte>>> CreateAuthenticate(AuthenticateScript script)
    {
        var calls = 0;
        return async cancellationToken =>
        {
            var call = Interlocked.Increment(ref calls);
            if (script.DelayMs > 0)
                await Task.Delay(script.DelayMs, cancellationToken).ConfigureAwait(false);
            if (script.Hang)
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            if (script.ThrowMessage is { } message)
                throw new InvalidOperationException(message);
            if (script.Token is not { } token)
                return Memory<byte>.Empty;
            return Encoding.UTF8.GetBytes(script.AppendCallNumber ? token + call.ToString(CultureInfo.InvariantCulture) : token);
        };
    }

    /// <summary>Appends <c>name=value</c>, each URL-encoded, to the query of <paramref name="address"/>.</summary>
    private static string WithQueryToken(string address, QueryToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token.Name);
        var builder = new UriBuilder(address);
        var pair = Uri.EscapeDataString(token.Name) + "=" + Uri.EscapeDataString(token.Value ?? "");
        var query = builder.Query.TrimStart('?');
        builder.Query = query.Length == 0 ? pair : query + "&" + pair;
        return builder.Uri.ToString();
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
