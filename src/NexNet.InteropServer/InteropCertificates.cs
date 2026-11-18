using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace NexNet.InteropServer;

/// <summary>
/// The F08 test certificates (test CA, server leaf with SAN <c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>), copied to
/// <c>certs/</c> in the output directory. TEST ONLY: every private key is public.
/// </summary>
public static class InteropCertificates
{
    /// <summary>
    /// Password of the PKCS#12 files.
    /// </summary>
    public const string PfxPassword = "certPass";

    /// <summary>
    /// Directory holding the certificate files: <c>certs/</c> next to this assembly.
    /// </summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "certs");

    /// <summary>
    /// Path of the PEM test CA certificate, for clients in other languages (Node <c>ca</c>, a Java trust store).
    /// </summary>
    public static string TestCaPemPath => Path.Combine(Directory, "test-ca.pem");

    /// <summary>
    /// Loads the server leaf certificate with its private key.
    /// </summary>
    public static X509Certificate2 LoadServerCertificate()
        => X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(Directory, "server.pfx"), PfxPassword);

    /// <summary>
    /// Loads the test CA certificate (no private key).
    /// </summary>
    public static X509Certificate2 LoadTestCa()
        => X509Certificate2.CreateFromPem(File.ReadAllText(TestCaPemPath));

    /// <summary>
    /// A certificate validation callback that trusts only <paramref name="trustedRoot"/> and keeps host name
    /// verification: a name mismatch or any other policy error besides an untrusted chain is rejected.
    /// </summary>
    public static RemoteCertificateValidationCallback CreateValidationCallback(X509Certificate2 trustedRoot)
    {
        ArgumentNullException.ThrowIfNull(trustedRoot);
        return (_, certificate, _, errors) =>
        {
            if (errors == SslPolicyErrors.None)
                return true;
            if (certificate == null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                return false;

            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(leaf);
        };
    }
}
