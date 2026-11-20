<#
.SYNOPSIS
  Release guard for NexNet (D18, D30, D31): lockstep versions, no publish path for the Java and JavaScript clients, and
  a NexNet.Generator package that carries the current client runtime.

.DESCRIPTION
  NuGet is the only release channel (D30). The Java and JavaScript clients are built, tested and versioned in lockstep
  with the NuGet packages, but never published to npm or to any Maven repository; NexNet.Generator embeds their runtime
  sources and emits them into the consumer's build (D31).

  The single version source is <Version> in src/NexNet/NexNet.csproj. Gradle reads it directly
  (clients/java/build-logic/src/main/kotlin/NexnetVersion.kt); -StampVersion writes it into the npm workspace.

  Modes (run from anywhere; paths are resolved from the repository root):

    release-guard.ps1                         Check versions and the absence of any npm/Maven publish path.
    release-guard.ps1 -Tag v1.2.3             Also check that the version is the tag's.
    release-guard.ps1 -PackageDirectory <dir>  Also check the packed NuGet packages in <dir>: NexNet, NexNet.Asp,
                                              NexNet.Quic and NexNet.Generator at the lockstep version, and the
                                              runtime sources NexNet.Generator embeds, byte for byte against the
                                              checked-in ones (D31).
    release-guard.ps1 -StampVersion           Write the NexNet.csproj version into clients/js (package.json files and
                                              package-lock.json). Run it after the version bump (llm-release.md step 4).

  Exit code 0 when every check passes; 1 with one line per failure otherwise.
#>
[CmdletBinding()]
param(
    [string] $Tag,
    [string] $PackageDirectory,
    [switch] $StampVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$failures = [System.Collections.Generic.List[string]]::new()
function Fail([string] $message) { $failures.Add($message) }
function RepoPath([string] $relative) { Join-Path $root $relative }
function ReadText([string] $relative) { [System.IO.File]::ReadAllText((RepoPath $relative)) }
function Rel([string] $full) { [System.IO.Path]::GetRelativePath($root, $full).Replace('\', '/') }

function CsprojVersion([string] $relative) {
    $match = [regex]::Match((ReadText $relative), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $match.Success) { Fail "$relative has no <Version> element."; return $null }
    $match.Groups[1].Value
}

$versionSource = 'src/NexNet/NexNet.csproj'
$version = CsprojVersion $versionSource
if (-not $version) { $failures | ForEach-Object { Write-Host "::error::$_" }; exit 1 }

$jsPackages = Get-ChildItem (RepoPath 'clients/js/packages') -Directory | Sort-Object Name |
    Where-Object { Test-Path (Join-Path $_.FullName 'package.json') }
$lockPath = 'clients/js/package-lock.json'
# The "version" line of one workspace entry ("packages/<dir>": { "name": ..., "version": ... }) in package-lock.json.
function LockVersionPattern([string] $dir) { '("packages/' + [regex]::Escape($dir) + '":\s*\{[^{}]*?"version":\s*")([^"]*)(")' }
$packageVersionPattern = '(?m)^(  "version":\s*")([^"]*)(")'

# ---------------------------------------------------------------------------------------------------------------------
if ($StampVersion) {
    foreach ($package in $jsPackages) {
        $file = Join-Path $package.FullName 'package.json'
        $text = [System.IO.File]::ReadAllText($file)
        $new = [regex]::Replace($text, $packageVersionPattern, { param($m) $m.Groups[1].Value + $version + $m.Groups[3].Value }, 'None')
        if ($new -ne $text) { [System.IO.File]::WriteAllText($file, $new); Write-Host "Stamped $version into $(Rel $file)" }
    }
    $lock = ReadText $lockPath
    $newLock = $lock
    foreach ($package in $jsPackages) {
        $newLock = [regex]::Replace($newLock, (LockVersionPattern $package.Name), { param($m) $m.Groups[1].Value + $version + $m.Groups[3].Value })
    }
    if ($newLock -ne $lock) { [System.IO.File]::WriteAllText((RepoPath $lockPath), $newLock); Write-Host "Stamped $version into $lockPath" }
}

# --- 1. Lockstep versions (D19, D30) ---------------------------------------------------------------------------------
foreach ($csproj in 'src/NexNet.Asp/NexNet.Asp.csproj', 'src/NexNet.Generator/NexNet.Generator.csproj', 'src/NexNet.Quic/NexNet.Quic.csproj') {
    $other = CsprojVersion $csproj
    if ($other -and $other -ne $version) { Fail "$csproj has version $other; $versionSource has $version." }
}
$lock = ReadText $lockPath
foreach ($package in $jsPackages) {
    $relative = "clients/js/packages/$($package.Name)/package.json"
    $json = ReadText $relative | ConvertFrom-Json
    if ($json.version -ne $version) {
        Fail "$relative has version $($json.version); $versionSource has $version. Run .github/scripts/release-guard.ps1 -StampVersion."
    }
    $lockMatch = [regex]::Match($lock, (LockVersionPattern $package.Name))
    if (-not $lockMatch.Success) { Fail "$lockPath has no versioned entry for packages/$($package.Name)." }
    elseif ($lockMatch.Groups[2].Value -ne $version) {
        Fail "$lockPath has version $($lockMatch.Groups[2].Value) for packages/$($package.Name); expected $version. Run -StampVersion."
    }
}
if ((ReadText 'clients/java/build-logic/src/main/kotlin/NexnetVersion.kt') -notmatch [regex]::Escape('src/NexNet/NexNet.csproj')) {
    Fail "clients/java/build-logic/src/main/kotlin/NexnetVersion.kt no longer reads the version from $versionSource."
}
if ($Tag) {
    if ($Tag -ne "v$version") { Fail "Tag $Tag does not match the version $version in $versionSource." }
}

# --- 2. No publish path to npm or Maven (D30) ------------------------------------------------------------------------
$workspace = ReadText 'clients/js/package.json' | ConvertFrom-Json
if ($workspace.PSObject.Properties['private']?.Value -ne $true) { Fail 'clients/js/package.json must be "private": true.' }
foreach ($package in $jsPackages) {
    $relative = "clients/js/packages/$($package.Name)/package.json"
    $json = ReadText $relative | ConvertFrom-Json
    if ($json.PSObject.Properties['private']?.Value -ne $true) { Fail "$relative must be ""private"": true (D30)." }
    if ($json.PSObject.Properties['publishConfig']) { Fail "$relative has a publishConfig; nothing is published to npm (D30)." }
}
$nexnetPackage = ReadText 'clients/js/packages/nexnet/package.json' | ConvertFrom-Json
$prepublish = $nexnetPackage.PSObject.Properties['scripts']?.Value?.PSObject.Properties['prepublishOnly']?.Value
if (-not $prepublish -or $prepublish -notmatch 'process\.exit\(1\)') {
    Fail 'clients/js/packages/nexnet/package.json needs the failing prepublishOnly script, so even npm publish --dry-run fails (D30).'
}
foreach ($npmrc in Get-ChildItem $root -Recurse -Force -Filter '.npmrc' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](node_modules|\.git|\.claude)[\\/]' }) {
    if ((Get-Content $npmrc.FullName -Raw) -match '(_authToken|_auth|//registry)') { Fail "$(Rel $npmrc.FullName) configures registry credentials." }
}

# Workflows: no npm/Gradle publish step and none of the secrets a publish to npm or Maven Central would need.
$workflowRules = [ordered]@{
    '\b(npm|pnpm|yarn)\s+publish\b'                                       = 'an npm publish command'
    'gradlew[^\n]*\bpublish'                                              = 'a Gradle publish task'
    '\bregistry-url\s*:'                                                  = 'setup-node registry-url (npm publish credentials)'
    '\b(NPM_TOKEN|NODE_AUTH_TOKEN|CENTRAL_\w+|SIGNING_\w+|OSSRH\w*|SONATYPE\w*|GPG_\w+|MAVEN_\w*(USER|PASS|TOKEN)\w*)\b' = 'an npm or Maven publishing secret'
}
foreach ($workflow in Get-ChildItem (RepoPath '.github/workflows') -File -Include '*.yml', '*.yaml' -Recurse) {
    $text = Get-Content $workflow.FullName -Raw
    foreach ($rule in $workflowRules.GetEnumerator()) {
        if ($text -match $rule.Key) { Fail "$(Rel $workflow.FullName) has $($rule.Value) ('$($Matches[0])'). Only NuGet is published (D30)." }
    }
}

# Gradle: the build-logic guard (NoRemotePublishing.kt) fails configuration at run time; these checks also cover the
# Android module, which only the Android CI job configures, and a guard that was removed.
$conventionPlugin = 'clients/java/build-logic/src/main/kotlin/nexnet.java-library.gradle.kts'
if ((ReadText $conventionPlugin) -notmatch 'forbidRemotePublishing\(\)') { Fail "$conventionPlugin no longer calls forbidRemotePublishing()." }
$gradleRules = [ordered]@{
    '(?m)^\s*(`signing`|signing\s*$|id\s*\(\s*"signing"\s*\))'                   = 'the signing plugin'
    '(?m)^\s*signing\s*\{'                                                  = 'a signing block'
    '(com\.vanniktech|gradle-nexus|jreleaser|nmcp|maven-central-publish|plugin-publish)' = 'a Maven Central publishing plugin'
    '(?i)(sonatype|ossrh|s01\.oss|maven\.pkg\.github)'                       = 'a remote publishing repository'
}
$javaRoot = RepoPath 'clients/java'
$gradleFiles = Get-ChildItem $javaRoot -Recurse -File -Include '*.gradle.kts', '*.gradle', 'gradle.properties' |
    Where-Object { $_.FullName -notmatch '[\\/](build|\.gradle|\.kotlin)[\\/]' -and $_.FullName -notmatch '[\\/]build-logic[\\/]src[\\/]' }
foreach ($gradleFile in $gradleFiles) {
    $text = Get-Content $gradleFile.FullName -Raw
    $relative = Rel $gradleFile.FullName
    foreach ($rule in $gradleRules.GetEnumerator()) {
        if ($text -match $rule.Key) { Fail "$relative has $($rule.Value) ('$($Matches[0].Trim())'). The Java clients are never published (D30)." }
    }
    if ($gradleFile.Name -eq 'gradle.properties' -and $text -match '(?m)^\s*(signing\.|mavenCentral|sonatype|ossrh)') {
        Fail "$relative has publishing credentials or signing settings (D30)."
    }
    # maven-publish is allowed only for the K28 consumer test of nexnet-client, whose single repository is local.
    if ($text -match 'maven-publish' -and $relative -ne 'clients/java/nexnet-client/build.gradle.kts') {
        Fail "$relative applies maven-publish; only nexnet-client may, for its local consumer test (K28)."
    }
}
$clientScript = ReadText 'clients/java/nexnet-client/build.gradle.kts'
$publishingBlock = [regex]::Match($clientScript, '(?s)publishing\s*\{.*?\n\}')
if ($publishingBlock.Success) {
    $repositories = [regex]::Matches($publishingBlock.Value, '\bmaven\s*\{[^}]*\}')
    $other = $repositories | Where-Object { $_.Value -notmatch 'name\s*=\s*"consumerTest"' -or $_.Value -notmatch 'url\s*=\s*uri\(consumerTestRepo\)' }
    if ($other -or [regex]::IsMatch($publishingBlock.Value, '\b(mavenCentral|mavenLocal|google)\s*\(')) {
        Fail 'clients/java/nexnet-client/build.gradle.kts publishes to a repository other than the local consumerTest one (D30).'
    }
}

# --- 3. The NuGet packages; the generator package carries the current runtime (D31, G09) ------------------------------
if ($PackageDirectory) {
    $directory = (Resolve-Path $PackageDirectory).Path
    foreach ($id in 'NexNet', 'NexNet.Asp', 'NexNet.Quic', 'NexNet.Generator') {
        if (-not (Test-Path (Join-Path $directory "$id.$version.nupkg"))) { Fail "$PackageDirectory has no $id.$version.nupkg." }
    }
    $packagePattern = '^NexNet(\.Asp|\.Quic|\.Generator)?\.' + [regex]::Escape($version) + '\.nupkg$'
    foreach ($package in Get-ChildItem $directory -File -Filter '*.nupkg' | Where-Object { $_.Name -notmatch $packagePattern }) {
        Fail "$PackageDirectory has an unexpected package $($package.Name)."
    }
    $GeneratorPackage = Join-Path $directory "NexNet.Generator.$version.nupkg"
}
if ($PackageDirectory -and (Test-Path $GeneratorPackage)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packagePath = (Resolve-Path $GeneratorPackage).Path
    $zip = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -eq 'NexNet.Generator.nuspec' }
        $dllEntry = $zip.Entries | Where-Object { $_.FullName -eq 'analyzers/dotnet/cs/NexNet.Generator.dll' }
        if (-not $nuspecEntry -or -not $dllEntry) {
            Fail "$GeneratorPackage is not a NexNet.Generator package (no NexNet.Generator.nuspec or analyzers/dotnet/cs/NexNet.Generator.dll)."
        }
        else {
            $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
            try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $packageVersion = $nuspec.package.metadata.version
            if ($packageVersion -ne $version) { Fail "$GeneratorPackage has version $packageVersion; expected $version." }

            $buffer = [System.IO.MemoryStream]::new()
            $stream = $dllEntry.Open()
            try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
            # Loaded from bytes only to read its manifest resources and name; no generator type is touched.
            $assembly = [System.Reflection.Assembly]::Load($buffer.ToArray())
            $assemblyVersion = $assembly.GetName().Version
            $stamp = '{0}.{1}.{2}' -f $assemblyVersion.Major, $assemblyVersion.Minor, [Math]::Max(0, $assemblyVersion.Build)
            # The stamp every emitted runtime file and NexusRuntimeVersion.VALUE carry (JsSyntax/JavaTarget GeneratorVersion).
            if ($stamp -ne $version) { Fail "NexNet.Generator.dll in $GeneratorPackage stamps the runtime as $stamp; expected $version." }

            # The same items as the EmbeddedResource globs in src/NexNet.Generator/NexNet.Generator.csproj.
            $expected = [System.Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
            function AddTree([string] $directory, [string] $filter, [string] $prefix) {
                $full = RepoPath $directory
                if (-not (Test-Path $full)) { Fail "Runtime source folder $directory is missing."; return }
                foreach ($item in Get-ChildItem $full -Recurse -File -Filter $filter) {
                    $expected[$prefix + [System.IO.Path]::GetRelativePath($full, $item.FullName).Replace('\', '/')] = $item.FullName
                }
            }
            AddTree 'clients/js/packages/nexnet/src' '*.js' 'NexNet.Runtime/javascript/'
            AddTree 'clients/js/packages/nexnet/types' '*.d.ts' 'NexNet.Runtime/javascript/'
            AddTree 'clients/java/nexnet-client/src/main/java' '*.java' 'NexNet.Runtime/java/'
            AddTree 'clients/java/nexnet-client-android/src/main/java' '*.java' 'NexNet.Runtime/java-android/'
            AddTree 'clients/java/nexnet-client-kotlin/src/main/kotlin' '*.kt' 'NexNet.Runtime/java-kotlin/'
            AddTree 'clients/java/nexnet-client-micrometer/src/main/java' '*.java' 'NexNet.Runtime/java-micrometer/'
            AddTree 'clients/java/nexnet-client-okhttp/src/main/java' '*.java' 'NexNet.Runtime/java-okhttp/'
            $expected['NexNet.Runtime/LICENSE'] = RepoPath 'LICENSE'

            $embedded = @{}
            foreach ($name in $assembly.GetManifestResourceNames()) {
                if ($name.StartsWith('NexNet.Runtime', [StringComparison]::Ordinal)) { $embedded[$name.Replace('\', '/')] = $name }
            }
            $missing = @($expected.Keys | Where-Object { -not $embedded.ContainsKey($_) })
            $extra = @($embedded.Keys | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object)
            $different = [System.Collections.Generic.List[string]]::new()
            foreach ($key in $expected.Keys) {
                if (-not $embedded.ContainsKey($key)) { continue }
                $resource = $assembly.GetManifestResourceStream($embedded[$key])
                $copy = [System.IO.MemoryStream]::new()
                try { $resource.CopyTo($copy) } finally { $resource.Dispose() }
                $packed = $copy.ToArray()
                $source = [System.IO.File]::ReadAllBytes($expected[$key])
                if (-not [System.Linq.Enumerable]::SequenceEqual($packed, $source)) { $different.Add($key) }
            }
            foreach ($key in $missing) { Fail "NexNet.Generator.dll does not embed the runtime file $key." }
            foreach ($key in $extra) { Fail "NexNet.Generator.dll embeds $key, which is not a checked-in runtime file." }
            foreach ($key in $different) { Fail "NexNet.Generator.dll embeds a stale copy of $key." }
            if (-not $missing -and -not $extra -and $different.Count -eq 0) {
                Write-Host "NexNet.Generator $packageVersion embeds the current runtime: $($expected.Count) files."
            }
        }
    }
    finally { $zip.Dispose() }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "::error::$failure" }
    exit 1
}
Write-Host "Release guard passed: version $version (NuGet, JavaScript and Java in lockstep); no npm or Maven publish path."
