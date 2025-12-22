using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexNet.Quic;
using NexNet.Transports;
using NexNet.Transports.HttpSocket;
using NexNet.Transports.WebSocket;

namespace NexNet.PerformanceBenchmarks.Config;

/// <summary>
/// Factory for creating transport configurations for benchmarking.
/// </summary>
public static class TransportFactory
{
    private static readonly object _portLock = new();
    private static int _currentPort = 15000;
    private static X509Certificate2? _cachedCertificate;

    /// <summary>
    /// Gets the next available port for TCP-based transports.
    /// </summary>
    public static int GetNextPort()
    {
        lock (_portLock)
        {
            return _currentPort++;
        }
    }

    /// <summary>
    /// Resets the port counter to the base port.
    /// </summary>
    public static void ResetPorts(int basePort = 15000)
    {
        lock (_portLock)
        {
            _currentPort = basePort;
        }
    }

    /// <summary>
    /// Checks if a transport type is available on the current system.
    /// </summary>
    public static bool IsTransportAvailable(TransportType type)
    {
        return type switch
        {
            TransportType.Uds => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            TransportType.Tcp => true,
            TransportType.Tls => true,
            TransportType.Quic => IsQuicAvailable(),
            TransportType.WebSocket => false, // Requires ASP.NET hosting
            TransportType.HttpSocket => false, // Requires ASP.NET hosting
            _ => false
        };
    }

    /// <summary>
    /// Gets a message explaining why a transport is unavailable.
    /// </summary>
    public static string GetUnavailableReason(TransportType type)
    {
        return type switch
        {
            TransportType.Quic when !IsQuicAvailable() => "QUIC is not supported on this system (requires Windows 11+ or Linux with libmsquic)",
            TransportType.WebSocket => "WebSocket transport requires ASP.NET hosting (not supported in standalone benchmarks)",
            TransportType.HttpSocket => "HttpSocket transport requires ASP.NET hosting (not supported in standalone benchmarks)",
            _ => "Unknown reason"
        };
    }

    /// <summary>
    /// Creates server and client configurations for the specified transport type.
    /// </summary>
    public static (ServerConfig ServerConfig, ClientConfig ClientConfig) CreateConfigs(TransportType type)
    {
        return type switch
        {
            TransportType.Uds => CreateUdsConfigs(),
            TransportType.Tcp => CreateTcpConfigs(),
            TransportType.Tls => CreateTlsConfigs(),
            TransportType.Quic => CreateQuicConfigs(),
            TransportType.WebSocket => throw new NotSupportedException("WebSocket requires ASP.NET hosting"),
            TransportType.HttpSocket => throw new NotSupportedException("HttpSocket requires ASP.NET hosting"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    /// <summary>
    /// Creates Unix Domain Socket configurations.
    /// </summary>
    private static (ServerConfig, ClientConfig) CreateUdsConfigs()
    {
        var socketPath = GetTempSocketPath();

        var serverConfig = new NexNet.Transports.Uds.UdsServerConfig
        {
            EndPoint = new UnixDomainSocketEndPoint(socketPath)
        };

        var clientConfig = new UdsClientConfig
        {
            EndPoint = new UnixDomainSocketEndPoint(socketPath)
        };

        return (serverConfig, clientConfig);
    }

    /// <summary>
    /// Creates TCP configurations.
    /// </summary>
    private static (ServerConfig, ClientConfig) CreateTcpConfigs()
    {
        var port = GetNextPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);

        var serverConfig = new TcpServerConfig
        {
            EndPoint = endpoint,
            TcpNoDelay = true
        };

        var clientConfig = new TcpClientConfig
        {
            EndPoint = endpoint,
            TcpNoDelay = true
        };

        return (serverConfig, clientConfig);
    }

    /// <summary>
    /// Creates TLS over TCP configurations.
    /// </summary>
    private static (ServerConfig, ClientConfig) CreateTlsConfigs()
    {
        var port = GetNextPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var certificate = GetOrCreateCertificate();

        var serverConfig = new TcpTlsServerConfig
        {
            EndPoint = endpoint,
            TcpNoDelay = true,
            SslServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13
            }
        };

        var clientConfig = new TcpTlsClientConfig
        {
            EndPoint = endpoint,
            TcpNoDelay = true,
            SslClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                      System.Security.Authentication.SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => true // Accept self-signed for benchmarks
            }
        };

        return (serverConfig, clientConfig);
    }

    /// <summary>
    /// Creates QUIC configurations.
    /// </summary>
    private static (ServerConfig, ClientConfig) CreateQuicConfigs()
    {
        var port = GetNextPort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var certificate = GetOrCreateCertificate();

        var serverConfig = new QuicServerConfig
        {
            EndPoint = endpoint,
            SslServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                ApplicationProtocols = [new SslApplicationProtocol("nexnet-benchmark")]
            }
        };

        var clientConfig = new QuicClientConfig
        {
            EndPoint = endpoint,
            SslClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // Accept self-signed for benchmarks
                ApplicationProtocols = [new SslApplicationProtocol("nexnet-benchmark")]
            }
        };

        return (serverConfig, clientConfig);
    }

    /// <summary>
    /// Creates or retrieves a cached self-signed certificate for TLS/QUIC.
    /// </summary>
    private static X509Certificate2 GetOrCreateCertificate()
    {
        if (_cachedCertificate != null)
            return _cachedCertificate;

        // Create a self-signed certificate for benchmarking
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=NexNet-Benchmark",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Add extensions for TLS server use
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        // Export and reimport with private key for Windows compatibility
        _cachedCertificate = X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.Exportable);

        return _cachedCertificate;
    }

    /// <summary>
    /// Gets a unique temporary socket path for UDS.
    /// </summary>
    private static string GetTempSocketPath()
    {
        var tempDir = Path.GetTempPath();
        var fileName = $"nexnet-bench-{Guid.NewGuid():N}.sock";
        return Path.Combine(tempDir, fileName);
    }

    /// <summary>
    /// Checks if QUIC is available on the current system.
    /// </summary>
    private static bool IsQuicAvailable()
    {
        try
        {
            return System.Net.Quic.QuicConnection.IsSupported;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cleans up any temporary resources (socket files, etc.).
    /// </summary>
    public static void Cleanup()
    {
        _cachedCertificate?.Dispose();
        _cachedCertificate = null;

        // Clean up any leftover socket files
        var tempDir = Path.GetTempPath();
        try
        {
            foreach (var file in Directory.GetFiles(tempDir, "nexnet-bench-*.sock"))
            {
                try { File.Delete(file); }
                catch { /* Ignore cleanup errors */ }
            }
        }
        catch { /* Ignore cleanup errors */ }
    }
}
