# NexNet test certificates (TEST ONLY)

**Every private key in this directory is public. Never trust `test-ca.pem` outside a test process, and never
install it in a machine or user certificate store.** The files exist so that every client language (C#, Java,
Node, browsers) can run TLS tests against a certificate that real TLS stacks accept: a CA-issued leaf with
subjectAltName entries, instead of a bare self-signed certificate.

Regenerate the whole set with:

```
pwsh tools/certs/generate-test-certs.ps1
```

The script uses .NET `CertificateRequest` (PowerShell 7.2+, no openssl) and rewrites the pin block below.
Commit every file it changes together. `*.pfx` is ignored by `src/.gitignore`, so add the PFX files with
`git add -f`.

## Files

| File | Contents |
|---|---|
| `test-ca.pem` | Test root CA certificate. RSA 3072, valid 10 years, `CA:true, pathlen:0` |
| `test-ca.key` | Test root CA private key, unencrypted PKCS#8 PEM. **Test only** |
| `server.pem` | Server leaf issued by the test CA (`CN=localhost`). RSA 2048, valid 5 years. SAN `DNS:localhost, IP:127.0.0.1, IP:::1`, EKU serverAuth |
| `server.key` | Server leaf private key, unencrypted PKCS#8 PEM |
| `server.pfx` | Server leaf + key, PKCS#12, password `certPass` |
| `client.pem` | Client leaf issued by the test CA (`CN=nexnet-test-client`). RSA 2048, valid 5 years, EKU clientAuth |
| `client.key` | Client leaf private key, unencrypted PKCS#8 PEM |
| `client.pfx` | Client leaf + key, PKCS#12, password `certPass` |

The integration tests copy this directory to `certs/` in their output directory. The samples and benchmarks
link `server.pfx` from here as well.

## How tests should use them

- **Validating tests** trust `test-ca.pem` explicitly (in .NET: `X509ChainPolicy` with
  `TrustMode = CustomRootTrust`; in Node: the `ca` option; in Java: a trust store holding only the test CA) and
  keep hostname verification on. Connect to `localhost`, `127.0.0.1` or `::1`.
- **Untrusted-server tests** use the default system trust, which does not contain the test CA, so the server
  is still rejected.
- **Pinning tests** use the SPKI pin below (sha256 over the DER SubjectPublicKeyInfo, base64).

## Pins

<!-- pins:start (written by tools/certs/generate-test-certs.ps1; do not edit by hand) -->
| Item | Value |
|---|---|
| Server leaf SPKI sha256 pin (base64) | `MZEa+vKvxw+5EI8S4C6W811NN39NrGjkKDakkDBwHdE=` |
| Server leaf certificate sha256 (hex) | `F22AF5CF9F7A0B0F99AD73BD2FF784DDE5E98C9F128C0A9F1D834952D903FF11` |
| Client leaf certificate sha256 (hex) | `DDD076444273A0F767D698AB173DB16E5DEEB37CE132ACEFBD09DDBA545F7705` |
| Test CA certificate sha256 (hex) | `5E0CE9E1DF8ECD3FA15846770745365E487F4C3B557D0975433F3A2692CF63D7` |
| Server and client leaf validity | 2026-11-17 to 2031-11-17 |
| Test CA validity | 2026-11-17 to 2036-11-17 |
<!-- pins:end -->
