using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace NexNet.Transports;

/// <summary>
/// One HTTP connection attempt for an <see cref="HttpClientConfig"/>: a <see cref="SocketsHttpHandler"/> built from
/// the config's TLS options that records the server certificate it accepted. Owns the handler and the recorded
/// certificate until disposed. Also records the address and port of the socket peer it connected to.
/// </summary>
internal sealed class HttpConnector : IDisposable
{
    private X509Certificate2? _serverCertificate;
    private IPEndPoint? _remoteEndPoint;

    public HttpMessageInvoker Invoker { get; }

    public HttpConnector(HttpClientConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(config.ConnectionTimeout),
            // Never follow a redirect of the upgrade request: it would carry the Authorization header and any
            // query token to another URL. A 3xx fails the connect (protocol-specification.md §13.6, §13.7).
            AllowAutoRedirect = false,
            SslOptions = CreateSslOptions(config.SslClientAuthenticationOptions),
            ConnectCallback = ConnectAsync,
        };
        Invoker = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    /// <summary>
    /// Hands the server certificate accepted during the TLS handshake to the caller, who then owns it.
    /// Null for a plain HTTP connection.
    /// </summary>
    public X509Certificate2? TakeServerCertificate() => Interlocked.Exchange(ref _serverCertificate, null);

    /// <summary>
    /// The address and port of the socket peer of the last connection the handler opened; null before it connects.
    /// This is the server, or the HTTP proxy when the handler connects through one.
    /// </summary>
    public (string? Address, int? Port) RemoteEndPoint
    {
        get
        {
            var endPoint = Volatile.Read(ref _remoteEndPoint);
            return endPoint == null ? (null, null) : EndPointHelper.ExtractEndPointInfo(endPoint);
        }
    }

    /// <summary>
    /// The handler's default connect (a dual-mode TCP socket with Nagle off), recording the peer it reached.
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            if (socket.RemoteEndPoint is IPEndPoint peer)
            {
                // A dual-mode socket reports an IPv4 peer as IPv4-mapped IPv6; report it as IPv4 like the TCP transport.
                var address = peer.Address.IsIPv4MappedToIPv6 ? peer.Address.MapToIPv4() : peer.Address;
                Volatile.Write(ref _remoteEndPoint, new IPEndPoint(address, peer.Port));
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Invoker.Dispose();
        Interlocked.Exchange(ref _serverCertificate, null)?.Dispose();
    }

    /// <summary>
    /// True if <paramref name="exception"/> or one of its inner exceptions is a TLS authentication failure:
    /// the server certificate was rejected or the server refused the client's.
    /// </summary>
    public static bool IsTlsAuthenticationFailure(Exception exception)
    {
        for (var e = exception; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Copies <paramref name="options"/> so the config's instance is never modified, and wraps its certificate
    /// validation so the accepted server certificate is recorded.
    /// </summary>
    private SslClientAuthenticationOptions CreateSslOptions(SslClientAuthenticationOptions? options)
    {
        var userValidation = options?.RemoteCertificateValidationCallback;
        var ssl = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
            {
                var accepted = userValidation?.Invoke(sender, certificate, chain, errors)
                               ?? errors == SslPolicyErrors.None;
                if (accepted && certificate != null)
                {
                    // The handshake's certificate belongs to the connection; keep an independent copy.
                    var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    Interlocked.Exchange(ref _serverCertificate, copy)?.Dispose();
                }

                return accepted;
            },
        };

        if (options == null)
            return ssl;

        ssl.AllowRenegotiation = options.AllowRenegotiation;
        ssl.AllowTlsResume = options.AllowTlsResume;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            ssl.AllowRsaPkcs1Padding = options.AllowRsaPkcs1Padding;
            ssl.AllowRsaPssPadding = options.AllowRsaPssPadding;
        }
        ssl.ApplicationProtocols = options.ApplicationProtocols;
        ssl.CertificateChainPolicy = options.CertificateChainPolicy;
        ssl.CertificateRevocationCheckMode = options.CertificateRevocationCheckMode;
        ssl.CipherSuitesPolicy = options.CipherSuitesPolicy;
        ssl.ClientCertificateContext = options.ClientCertificateContext;
        ssl.ClientCertificates = options.ClientCertificates;
        ssl.EnabledSslProtocols = options.EnabledSslProtocols;
        ssl.LocalCertificateSelectionCallback = options.LocalCertificateSelectionCallback;
        ssl.TargetHost = options.TargetHost;
        return ssl;
    }
}
