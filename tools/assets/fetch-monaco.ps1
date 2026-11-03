<#
.SYNOPSIS
  Downloads monaco-editor and extracts min/vs into src/AgentStudio.Desktop/Assets/monaco/vs.
.DESCRIPTION
  The tarball's SHA-512 must equal BOTH the integrity pinned in this script and dist.integrity from the npm
  registry before anything is extracted, so a compromised registry cannot slip a different tarball through.
  Idempotent: does nothing if the pinned version is already installed (use -Force to redo).
  -WhatIf only reports what would happen. Works on Windows PowerShell 5.1 and PowerShell 7.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Version = '0.57.0',
    [string] $Destination,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
# Resolved here, not as the parameter default: Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults.
if (-not $Destination) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $Destination = Join-Path $scriptDir '..\..\src\AgentStudio.Desktop\Assets\monaco'
}
$ProgressPreference = 'SilentlyContinue'  # Windows PowerShell 5.1 renders progress so slowly that downloads crawl
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# Pinned SHA-512 integrity (SRI) of the npm tarball per version. Source: dist.integrity of
# https://registry.npmjs.org/monaco-editor/0.57.0, checked on 2026-10-15. Bumping -Version requires adding its value here.
$pinnedIntegrity = @{
    '0.57.0' = 'sha512-5BkI9KGoqrNvBGUe15/QlZq3OooZ8WLg1AxTpaqHRCP3HNpzPPZKE2EDz8M7c+VRmCeUw1Brp4cx/PWm3kI/5A=='
}
$Destination = [System.IO.Path]::GetFullPath($Destination)
$vsDir = Join-Path $Destination 'vs'
$marker = Join-Path $Destination 'version.txt'

if (-not $Force -and (Test-Path (Join-Path $vsDir 'loader.js')) -and (Test-Path $marker) -and
    ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $Version)) {
    Write-Host "monaco-editor $Version already present in $vsDir"
    return
}

if (-not $PSCmdlet.ShouldProcess($vsDir, "Download and extract monaco-editor $Version")) { return }

$pinned = $pinnedIntegrity[$Version]
if (-not $pinned) { throw "No pinned integrity for monaco-editor $Version. Add it to the pinnedIntegrity table in this script after verifying it." }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('monaco-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $meta = Invoke-RestMethod -Uri "https://registry.npmjs.org/monaco-editor/$Version"
    $integrity = [string] $meta.dist.integrity
    if ($integrity -ne $pinned) { throw "The registry's dist.integrity for monaco-editor $Version ($integrity) differs from the pinned value ($pinned)." }

    $tgz = Join-Path $temp 'monaco.tgz'
    Invoke-WebRequest -Uri "https://registry.npmjs.org/monaco-editor/-/monaco-editor-$Version.tgz" -OutFile $tgz -UseBasicParsing

    $sha = [System.Security.Cryptography.SHA512]::Create()
    $actual = 'sha512-' + [Convert]::ToBase64String($sha.ComputeHash([System.IO.File]::ReadAllBytes($tgz)))
    $sha.Dispose()
    if ($actual -ne $pinned) { throw "Integrity mismatch for monaco-editor $Version. Expected $pinned, got $actual." }
    Write-Host "Integrity verified (pinned and registry): $pinned"

    $extract = Join-Path $temp 'x'
    New-Item -ItemType Directory -Path $extract | Out-Null
    # Use Windows' bsdtar explicitly: from Git Bash, 'tar' resolves to GNU tar, which reads 'C:' as a remote host.
    $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tarExe)) { $tarExe = 'tar' }
    & $tarExe -xzf $tgz -C $extract 'package/min/vs'
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

    $source = Join-Path $extract 'package\min\vs'
    if (-not (Test-Path (Join-Path $source 'loader.js'))) { throw 'The package does not contain min/vs/loader.js.' }

    if (Test-Path $vsDir) { Remove-Item -LiteralPath $vsDir -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Move-Item -LiteralPath $source -Destination $vsDir
    Set-Content -LiteralPath $marker -Value $Version -NoNewline

    $bytes = (Get-ChildItem -LiteralPath $vsDir -Recurse -File | Measure-Object Length -Sum).Sum
    Write-Host ('monaco-editor {0} extracted to {1} ({2:N1} MB)' -f $Version, $vsDir, ($bytes / 1MB))
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
