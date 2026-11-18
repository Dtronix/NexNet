#Requires -Version 7.2
using namespace System.Security.Cryptography
using namespace System.Security.Cryptography.X509Certificates

<#
.SYNOPSIS
    Regenerates the NexNet TEST-ONLY certificate set in src/NexNet.IntegrationTests/certs.

.DESCRIPTION
    Produces, with .NET CertificateRequest (no openssl needed):
      test-ca.pem / test-ca.key   Test root CA, RSA 3072, valid 10 years.
      server.pem / server.key     Server leaf issued by the test CA, RSA 2048, valid 5 years,
                                  SAN DNS:localhost, IP:127.0.0.1, IP:::1, EKU serverAuth.
      server.pfx                  Server leaf + key, PKCS#12, password "certPass".
      client.pem / client.key     Client leaf issued by the test CA, RSA 2048, valid 5 years, EKU clientAuth.
      client.pfx                  Client leaf + key, PKCS#12, password "certPass".
    Keys are unencrypted PKCS#8 PEM. Every key here is public and for tests only.

    The script then rewrites the pin block in certs/README.md with the server leaf's SPKI
    sha256 pin and the certificate fingerprints. Re-running it replaces every file (new keys).

.PARAMETER OutDir
    Output directory. Defaults to src/NexNet.IntegrationTests/certs relative to the repo root.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..' '..' 'src' 'NexNet.IntegrationTests' 'certs')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest


$OutDir = [System.IO.Path]::GetFullPath($OutDir)
[System.IO.Directory]::CreateDirectory($OutDir) | Out-Null

$PfxPassword = 'certPass'
$Sha256 = [HashAlgorithmName]::SHA256
$Pkcs1 = [RSASignaturePadding]::Pkcs1
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$NotBefore = [DateTimeOffset]::new([DateTime]::UtcNow.Date.AddDays(-1), [TimeSpan]::Zero)
$KU = [X509KeyUsageFlags]
$LF = [string][char]10

function New-Serial {
    $bytes = [byte[]]::new(16)
    [RandomNumberGenerator]::Fill($bytes)
    $bytes[0] = $bytes[0] -band 0x7F  # keep the serial positive
    if ($bytes[0] -eq 0) { $bytes[0] = 1 }
    return , $bytes
}

function Write-Text([string]$name, [string]$text) {
    $text = $text.Replace([string][char]13 + $LF, $LF)
    if (-not $text.EndsWith($LF)) { $text += $LF }
    [System.IO.File]::WriteAllText((Join-Path $OutDir $name), $text, $Utf8NoBom)
}

function Write-Leaf([string]$baseName, $cert, $key) {
    Write-Text "$baseName.pem" $cert.ExportCertificatePem()
    Write-Text "$baseName.key" $key.ExportPkcs8PrivateKeyPem()
    $withKey = [RSACertificateExtensions]::CopyWithPrivateKey($cert, $key)
    try {
        [System.IO.File]::WriteAllBytes((Join-Path $OutDir "$baseName.pfx"),
            $withKey.Export([X509ContentType]::Pfx, $PfxPassword))
    }
    finally { $withKey.Dispose() }
}

# ---------- Test CA ----------
$caKey = [RSA]::Create(3072)
$caReq = [CertificateRequest]::new('CN=NexNet Test CA (TEST ONLY), O=Dtronix NexNet Tests', $caKey, $Sha256, $Pkcs1)
$caReq.CertificateExtensions.Add([X509BasicConstraintsExtension]::new($true, $true, 0, $true))
$caReq.CertificateExtensions.Add([X509KeyUsageExtension]::new($KU::KeyCertSign -bor $KU::CrlSign -bor $KU::DigitalSignature, $true))
$caSki = [X509SubjectKeyIdentifierExtension]::new($caReq.PublicKey, $false)
$caReq.CertificateExtensions.Add($caSki)
$caReq.CertificateExtensions.Add([X509AuthorityKeyIdentifierExtension]::CreateFromSubjectKeyIdentifier($caSki))
$ca = $caReq.CreateSelfSigned($NotBefore, $NotBefore.AddYears(10))
Write-Text 'test-ca.pem' $ca.ExportCertificatePem()
Write-Text 'test-ca.key' $caKey.ExportPkcs8PrivateKeyPem()

$caAki = [X509AuthorityKeyIdentifierExtension]::CreateFromCertificate($ca, $true, $false)

function New-Leaf([string]$subject, [string]$ekuOid, [string]$ekuName, $san) {
    $key = [RSA]::Create(2048)
    $req = [CertificateRequest]::new($subject, $key, $Sha256, $Pkcs1)
    $req.CertificateExtensions.Add([X509BasicConstraintsExtension]::new($false, $false, 0, $true))
    $req.CertificateExtensions.Add([X509KeyUsageExtension]::new($KU::DigitalSignature -bor $KU::KeyEncipherment, $true))
    $ekus = [OidCollection]::new()
    $ekus.Add([Oid]::new($ekuOid, $ekuName)) | Out-Null
    $req.CertificateExtensions.Add([X509EnhancedKeyUsageExtension]::new($ekus, $false))
    $req.CertificateExtensions.Add([X509SubjectKeyIdentifierExtension]::new($req.PublicKey, $false))
    $req.CertificateExtensions.Add($caAki)
    if ($null -ne $san) { $req.CertificateExtensions.Add($san) }
    $cert = $req.Create($ca, $NotBefore, $NotBefore.AddYears(5), (New-Serial))
    return @($cert, $key)
}

# ---------- Server leaf ----------
$sanBuilder = [SubjectAlternativeNameBuilder]::new()
$sanBuilder.AddDnsName('localhost')
$sanBuilder.AddIpAddress([System.Net.IPAddress]::Parse('127.0.0.1'))
$sanBuilder.AddIpAddress([System.Net.IPAddress]::IPv6Loopback)
$serverSan = $sanBuilder.Build($false)
$server, $serverKey = New-Leaf 'CN=localhost, O=Dtronix NexNet Tests' '1.3.6.1.5.5.7.3.1' 'Server Authentication' $serverSan
Write-Leaf 'server' $server $serverKey

# ---------- Client leaf ----------
$client, $clientKey = New-Leaf 'CN=nexnet-test-client, O=Dtronix NexNet Tests' '1.3.6.1.5.5.7.3.2' 'Client Authentication' $null
Write-Leaf 'client' $client $clientKey

# ---------- README pin block ----------
$spkiPin = [Convert]::ToBase64String([SHA256]::HashData($server.PublicKey.ExportSubjectPublicKeyInfo()))
$tick = [string][char]96
function Row([string]$name, [string]$value) { "| $name | $tick$value$tick |" }
function Day($d) { $d.ToUniversalTime().ToString('yyyy-MM-dd') }

$readme = Join-Path $OutDir 'README.md'
if (-not (Test-Path $readme)) { throw "README.md not found in $OutDir; it must contain a pins:start/pins:end block." }
$block = @(
    '<!-- pins:start (written by tools/certs/generate-test-certs.ps1; do not edit by hand) -->'
    '| Item | Value |'
    '|---|---|'
    (Row 'Server leaf SPKI sha256 pin (base64)' $spkiPin)
    (Row 'Server leaf certificate sha256 (hex)' $server.GetCertHashString($Sha256))
    (Row 'Client leaf certificate sha256 (hex)' $client.GetCertHashString($Sha256))
    (Row 'Test CA certificate sha256 (hex)' $ca.GetCertHashString($Sha256))
    "| Server and client leaf validity | $(Day $server.NotBefore) to $(Day $server.NotAfter) |"
    "| Test CA validity | $(Day $ca.NotBefore) to $(Day $ca.NotAfter) |"
    '<!-- pins:end -->'
) -join $LF
$text = [System.IO.File]::ReadAllText($readme)
$pattern = '(?s)<!-- pins:start.*?<!-- pins:end -->'
if (-not [regex]::IsMatch($text, $pattern)) { throw 'README.md has no pins:start/pins:end block.' }
$text = [regex]::Replace($text, $pattern, [System.Text.RegularExpressions.MatchEvaluator] { param($m) $block })
Write-Text 'README.md' $text

Write-Host "Wrote test certificates to $OutDir"
Write-Host "Server SPKI sha256 pin (base64): $spkiPin"

foreach ($d in @($ca, $server, $client)) { $d.Dispose() }
foreach ($k in @($caKey, $serverKey, $clientKey)) { $k.Dispose() }
