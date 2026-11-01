using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace NexNet.Transports;

/// <summary>
/// One HTTP connection attempt for an <see cref="HttpClientConfig"/>: a <see cref="SocketsHttpHandler"/> built from
/// the config's TLS options that records the server certificate it accepted. Owns the handler and the recorded
/// certificate until disposed.
/// </summary>
internal sealed class HttpConnector : IDisposable
{
    private X509Certificate2? _serverCertificate;

    public HttpMessageInvoker Invoker { get; }

    public HttpConnector(HttpClientConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(config.ConnectionTimeout),
            SslOptions = CreateSslOptions(config.SslClientAuthenticationOptions),
        };
        Invoker = new HttpMessageInvoker(handler, disposeHandler: true);
    }

    /// <summary>
    /// Hands the server certificate accepted during the TLS handshake to the caller, who then owns it.
    /// Null for a plain HTTP connection.
    /// </summary>
    public X509Certificate2? TakeServerCertificate() => Interlocked.Exchange(ref _serverCertificate, null);

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
