<#
.SYNOPSIS
  Downloads xterm.js and its fit addon from the npm registry into src/AgentStudio.Desktop/Assets/xterm,
  verifying each tarball against BOTH the SHA-512 integrity pinned in this script and the registry's dist.integrity,
  so a compromised registry cannot slip a different tarball through. Both packages are MIT.
  The downloaded files are NOT versioned (see .gitignore). Idempotent: does nothing when the files are present and
  version.txt matches the pinned versions; a version bump re-downloads.
  Works on Windows PowerShell 5.1 and PowerShell 7.
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# Resolved here, not as the parameter default: Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults.
if (-not $Destination) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }
    $Destination = Join-Path $scriptDir '..\..\src\AgentStudio.Desktop\Assets\xterm'
}
$ProgressPreference = 'SilentlyContinue'  # Windows PowerShell 5.1 renders progress so slowly that downloads crawl
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# Pinned SHA-512 integrity (SRI) per package tarball. Source: dist.integrity of
# https://registry.npmjs.org/@xterm/xterm/6.0.0 and https://registry.npmjs.org/@xterm/addon-fit/0.11.0, checked on
# 2026-10-15. Bumping a version requires updating its Integrity here after verifying the new value.
$packages = @(
    @{ Name = '@xterm/xterm'; Version = '6.0.0'; Integrity = 'sha512-TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg=='; Files = @{ 'package/lib/xterm.js' = 'xterm.js'; 'package/css/xterm.css' = 'xterm.css'; 'package/LICENSE' = 'LICENSE-xterm.txt' } },
    @{ Name = '@xterm/addon-fit'; Version = '0.11.0'; Integrity = 'sha512-jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g=='; Files = @{ 'package/lib/addon-fit.js' = 'addon-fit.js'; 'package/LICENSE' = 'LICENSE-addon-fit.txt' } }
)

$Destination = [System.IO.Path]::GetFullPath($Destination)
$expected = $packages | ForEach-Object { $_.Files.Values } | ForEach-Object { $_ }
$missing = @($expected | Where-Object { -not (Test-Path (Join-Path $Destination $_)) })
$marker = Join-Path $Destination 'version.txt'
$versions = ($packages | ForEach-Object { "$($_.Name)@$($_.Version)" }) -join ';'
$installed = if (Test-Path $marker) { (Get-Content -LiteralPath $marker -Raw).Trim() } else { '' }
if (-not $Force -and $missing.Count -eq 0 -and $installed -eq $versions) {
    Write-Host "xterm assets already present in $Destination"
    return
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("as-xterm-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    foreach ($p in $packages) {
        $meta = Invoke-RestMethod -Uri "https://registry.npmjs.org/$($p.Name)/$($p.Version)"
        if ($meta.license -ne 'MIT') { throw "Unexpected license '$($meta.license)' for $($p.Name)" }
        $integrity = [string]$meta.dist.integrity
        if ($integrity -ne $p.Integrity) { throw "The registry's dist.integrity for $($p.Name)@$($p.Version) ($integrity) differs from the pinned value ($($p.Integrity))." }

        $safeName = $p.Name -replace '[@/]', '_'
        $tgz = Join-Path $work ($safeName + '.tgz')
        Invoke-WebRequest -Uri $meta.dist.tarball -OutFile $tgz -UseBasicParsing
        $actual = 'sha512-' + [Convert]::ToBase64String([System.Security.Cryptography.SHA512]::Create().ComputeHash([System.IO.File]::ReadAllBytes($tgz)))
        if ($actual -ne $p.Integrity) { throw "Integrity mismatch for $($p.Name)@$($p.Version): expected $($p.Integrity), got $actual" }
        Write-Host "OK integrity (pinned and registry) $($p.Name)@$($p.Version)"

        $extract = Join-Path $work ($safeName + '-x')
        New-Item -ItemType Directory -Force -Path $extract | Out-Null
        # Use Windows' bsdtar explicitly: from Git Bash, 'tar' resolves to GNU tar, which reads 'C:' as a remote host.
        $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path $tarExe)) { $tarExe = 'tar' }
        & $tarExe -xzf $tgz -C $extract
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $($p.Name)" }
        foreach ($entry in $p.Files.GetEnumerator()) {
            $src = Join-Path $extract $entry.Key
            if (-not (Test-Path $src)) { throw "Missing $($entry.Key) in $($p.Name)@$($p.Version)" }
            Copy-Item $src (Join-Path $Destination $entry.Value) -Force
        }
    }
    Set-Content -LiteralPath $marker -Value $versions -NoNewline
    Write-Host "xterm assets written to $Destination"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
