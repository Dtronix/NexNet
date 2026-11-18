#!/usr/bin/env pwsh
# Reports which Java/JavaScript toolchains are installed for NexNet multi-language work.
#
# Usage:
#   pwsh tools/setup/check-toolchain.ps1 [--require java,node,...]
#
# Prints found/missing and the version for: dotnet, java/javac (21+), node (22+), npm,
# ANDROID_HOME and Playwright. Exits 0 unless --require names a tool that is missing or too old.
# Recognized --require names: dotnet, java, node, npm, android, playwright.
# See docs/articles/contributing-multilang.md.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$known = @('dotnet', 'java', 'node', 'npm', 'android', 'playwright')
$required = New-Object System.Collections.Generic.List[string]

function Add-Required([object[]] $values) {
    foreach ($v in $values) {
        foreach ($name in ([string]$v).Split(',')) {
            $n = $name.Trim().ToLowerInvariant()
            if ($n -eq '') { continue }
            if ($known -notcontains $n) {
                [Console]::Error.WriteLine("check-toolchain: unknown tool '$n' in --require (known: $($known -join ', '))")
                exit 2
            }
            if (-not $required.Contains($n)) { $required.Add($n) }
        }
    }
}

$i = 0
while ($i -lt $args.Count) {
    $a = $args[$i]
    if ($a -is [string] -and $a -like '--require=*') {
        Add-Required @($a.Substring('--require='.Length))
    } elseif ($a -is [string] -and ($a -eq '--require' -or $a -eq '-require')) {
        $i++
        if ($i -ge $args.Count) {
            [Console]::Error.WriteLine('check-toolchain: --require needs a value, e.g. --require java,node')
            exit 2
        }
        Add-Required @($args[$i])
    } elseif ($a -is [string] -and ($a -eq '--help' -or $a -eq '-h')) {
        Write-Output 'Usage: check-toolchain.ps1 [--require dotnet,java,node,npm,android,playwright]'
        exit 0
    } else {
        [Console]::Error.WriteLine("check-toolchain: unknown argument '$a'")
        exit 2
    }
    $i++
}

# name -> $true (ok) / $false (missing or too old)
$status = @{}
$problems = @{}

function Get-Major([string] $version) {
    if ($version -match '^1\.(\d+)') { return [int]$Matches[1] }   # legacy 1.8.0_xxx
    if ($version -match '^(\d+)') { return [int]$Matches[1] }
    return -1
}

function Invoke-Capture([string] $exe, [string[]] $arguments) {
    # Merges stderr into stdout (java -version prints to stderr) without tripping $ErrorActionPreference.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $exe @arguments 2>&1 | ForEach-Object { "$_" }
        return @{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
    } catch {
        return @{ Code = -1; Text = "$_" }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Write-Row([string] $name, [bool] $ok, [string] $detail) {
    $mark = if ($ok) { 'found  ' } else { 'MISSING' }
    Write-Output ('  {0}  {1,-12} {2}' -f $mark, $name, $detail)
}

function Find-Exe([string] $name) {
    $cmd = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    return $null
}

Write-Output 'NexNet toolchain check'
Write-Output ''

# dotnet
$dotnet = Find-Exe 'dotnet'
if ($dotnet) {
    $r = Invoke-Capture $dotnet @('--version')
    $v = ($r.Text -split "`n")[0].Trim()
    Write-Row 'dotnet' $true "$v ($dotnet)"
    $status['dotnet'] = $true
} else {
    Write-Row 'dotnet' $false 'not on PATH; install the .NET SDK'
    $status['dotnet'] = $false
    $problems['dotnet'] = 'dotnet not found on PATH; install the .NET SDK (https://dot.net)'
}

# java + javac (major 21+)
$javaOk = $true
$javaProblems = @()
foreach ($tool in @('java', 'javac')) {
    $exe = $null
    if ($env:JAVA_HOME) {
        foreach ($cand in @("$env:JAVA_HOME/bin/$tool.exe", "$env:JAVA_HOME/bin/$tool")) {
            if (Test-Path -LiteralPath $cand -PathType Leaf) { $exe = $cand; break }
        }
    }
    if (-not $exe) { $exe = Find-Exe $tool }
    if (-not $exe) {
        Write-Row $tool $false 'not found (set JAVA_HOME or put it on PATH); need JDK 21+'
        $javaOk = $false
        $javaProblems += "$tool not found"
        continue
    }
    $r = Invoke-Capture $exe @('-version')
    $v = $null
    if ($r.Text -match 'version "([^"]+)"') { $v = $Matches[1] }
    elseif ($r.Text -match '(?m)^javac\s+(\S+)') { $v = $Matches[1] }
    if (-not $v) {
        Write-Row $tool $false "could not read version ($exe)"
        $javaOk = $false
        $javaProblems += "$tool version unreadable"
        continue
    }
    $major = Get-Major $v
    if ($major -ge 21) {
        Write-Row $tool $true "$v ($exe)"
    } else {
        Write-Row $tool $false "$v is too old; need 21+ ($exe)"
        $javaOk = $false
        $javaProblems += "$tool $v is older than 21"
    }
}
$status['java'] = $javaOk
if (-not $javaOk) { $problems['java'] = ($javaProblems -join '; ') + '. Install JDK 21+, e.g. winget install EclipseAdoptium.Temurin.21.JDK' }

# node (22+)
$node = Find-Exe 'node'
if ($node) {
    $r = Invoke-Capture $node @('--version')
    $v = ($r.Text -split "`n")[0].Trim().TrimStart('v')
    $major = Get-Major $v
    if ($major -ge 22) {
        Write-Row 'node' $true "$v ($node)"
        $status['node'] = $true
    } else {
        Write-Row 'node' $false "$v is too old; need 22+ ($node)"
        $status['node'] = $false
        $problems['node'] = "node $v is older than 22; install Node 22+ (24 LTS recommended)"
    }
} else {
    Write-Row 'node' $false 'not on PATH; need Node 22+ (24 LTS recommended)'
    $status['node'] = $false
    $problems['node'] = 'node not found on PATH; install Node 22+ (24 LTS recommended)'
}

# npm
$npm = Find-Exe 'npm'
if ($npm) {
    $r = Invoke-Capture $npm @('--version')
    $v = ($r.Text -split "`n")[0].Trim()
    Write-Row 'npm' $true "$v ($npm)"
    $status['npm'] = $true
} else {
    Write-Row 'npm' $false 'not on PATH; npm ships with Node'
    $status['npm'] = $false
    $problems['npm'] = 'npm not found on PATH; it ships with Node'
}

# ANDROID_HOME (only needed for nexnet-client-android work)
if ($env:ANDROID_HOME -and (Test-Path -LiteralPath $env:ANDROID_HOME -PathType Container)) {
    Write-Row 'ANDROID_HOME' $true $env:ANDROID_HOME
    $status['android'] = $true
} elseif ($env:ANDROID_HOME) {
    Write-Row 'ANDROID_HOME' $false "set to '$env:ANDROID_HOME' but the directory does not exist"
    $status['android'] = $false
    $problems['android'] = "ANDROID_HOME points to a missing directory: $env:ANDROID_HOME"
} else {
    Write-Row 'ANDROID_HOME' $false 'not set (only needed for nexnet-client-android work)'
    $status['android'] = $false
    $problems['android'] = 'ANDROID_HOME is not set; install the Android command-line tools'
}

# Playwright (via npx, without installing anything)
$npx = Find-Exe 'npx'
if ($npx) {
    $r = Invoke-Capture $npx @('--no-install', 'playwright', '--version')
    $line = (($r.Text -split "`n") | Where-Object { $_ -match '^Version\s+\S+' } | Select-Object -First 1)
    if ($r.Code -eq 0 -and $line) {
        Write-Row 'playwright' $true ($line -replace '^Version\s+', '')
        $status['playwright'] = $true
    } else {
        Write-Row 'playwright' $false 'not installed (installs with clients/js; then npx playwright install chromium)'
        $status['playwright'] = $false
        $problems['playwright'] = 'playwright not found via npx; run npm ci --prefix clients/js, then npx playwright install chromium'
    }
} else {
    Write-Row 'playwright' $false 'npx not on PATH'
    $status['playwright'] = $false
    $problems['playwright'] = 'npx not found on PATH; install Node 22+'
}

Write-Output ''
$failed = @($required | Where-Object { -not $status[$_] })
if ($failed.Count -gt 0) {
    foreach ($f in $failed) {
        [Console]::Error.WriteLine("check-toolchain: required tool '$f' is not usable: $($problems[$f])")
    }
    [Console]::Error.WriteLine('See docs/articles/contributing-multilang.md')
    exit 1
}
if ($required.Count -gt 0) {
    Write-Output "All required tools present: $($required -join ', ')"
} else {
    Write-Output 'Only the .NET SDK is needed for C# work. Use --require to enforce others.'
}
exit 0
