using System.Collections.Generic;
using System.Net.Quic;
using System.Net.Security;
using NexNet.Transports;
#pragma warning disable CA1416

namespace NexNet.Quic;

internal static class QuicHelpers
{
    /// <summary>
    /// The application protocol NexNet negotiates over QUIC.
    /// </summary>
    public static readonly SslApplicationProtocol Alpn = new("nn1");

    public static TransportError GetTransportError(QuicError error)
    {
        return error switch
        {
            QuicError.InternalError => TransportError.InternalError,
            QuicError.ConnectionAborted => TransportError.ConnectionAborted,
            QuicError.StreamAborted => TransportError.StreamAborted,
            QuicError.AlpnInUse => TransportError.AddressInUse,
            QuicError.ConnectionTimeout => TransportError.ConnectionTimeout,
            QuicError.ConnectionRefused => TransportError.ConnectionRefused,
            QuicError.VersionNegotiationError => TransportError.VersionNegotiationError,
            QuicError.ConnectionIdle => TransportError.ConnectionIdle,
            QuicError.OperationAborted => TransportError.OperationAborted,
            QuicError.Success => TransportError.Success,
            QuicError.TransportError => TransportError.ProtocolError,
            QuicError.CallbackError => TransportError.InternalError,
            _ => TransportError.InternalError
        };
    }

    /// <summary>
    /// Copies <paramref name="options"/> with the NexNet ALPN as its only application protocol. The caller's object
    /// is left unchanged, so options shared with a TLS listener do not pick up the QUIC ALPN.
    /// </summary>
    public static SslServerAuthenticationOptions WithNexNetAlpn(SslServerAuthenticationOptions options)
    {
        return new SslServerAuthenticationOptions
        {
            AllowRenegotiation = options.AllowRenegotiation,
            AllowTlsResume = options.AllowTlsResume,
            AllowRsaPssPadding = options.AllowRsaPssPadding,
            AllowRsaPkcs1Padding = options.AllowRsaPkcs1Padding,
            ApplicationProtocols = new List<SslApplicationProtocol> { Alpn },
            CertificateChainPolicy = options.CertificateChainPolicy,
            CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
            CipherSuitesPolicy = options.CipherSuitesPolicy,
            ClientCertificateRequired = options.ClientCertificateRequired,
            EnabledSslProtocols = options.EnabledSslProtocols,
            EncryptionPolicy = options.EncryptionPolicy,
            RemoteCertificateValidationCallback = options.RemoteCertificateValidationCallback,
            ServerCertificate = options.ServerCertificate,
            ServerCertificateContext = options.ServerCertificateContext,
            ServerCertificateSelectionCallback = options.ServerCertificateSelectionCallback,
        };
    }

    /// <summary>
    /// Copies <paramref name="options"/> with the NexNet ALPN as its only application protocol. The caller's object
    /// is left unchanged.
    /// </summary>
    public static SslClientAuthenticationOptions WithNexNetAlpn(SslClientAuthenticationOptions options)
    {
        return new SslClientAuthenticationOptions
        {
            AllowRenegotiation = options.AllowRenegotiation,
            AllowTlsResume = options.AllowTlsResume,
            AllowRsaPssPadding = options.AllowRsaPssPadding,
            AllowRsaPkcs1Padding = options.AllowRsaPkcs1Padding,
            ApplicationProtocols = new List<SslApplicationProtocol> { Alpn },
            CertificateChainPolicy = options.CertificateChainPolicy,
            CertificateRevocationCheckMode = options.CertificateRevocationCheckMode,
            CipherSuitesPolicy = options.CipherSuitesPolicy,
            ClientCertificateContext = options.ClientCertificateContext,
            ClientCertificates = options.ClientCertificates,
            EnabledSslProtocols = options.EnabledSslProtocols,
            EncryptionPolicy = options.EncryptionPolicy,
            LocalCertificateSelectionCallback = options.LocalCertificateSelectionCallback,
            RemoteCertificateValidationCallback = options.RemoteCertificateValidationCallback,
            TargetHost = options.TargetHost,
        };
    }
}
