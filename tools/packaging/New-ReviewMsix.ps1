#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $DestinationRoot,
    [Parameter(Mandatory)][string] $LogoDirectory,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $Publisher,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string] $ExpectedPayloadHash
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReviewMsix.Validation.psm1') -Force
$delivery = $null
$result = [ordered]@{ outcome='Rejected'; signed=$false; installed=$false; version=$Version; publisher=$Publisher; failure=$null }
$exitCode = 1
try {
    if (-not $IsWindows) { throw 'MSIX review packaging requires Windows and its installed SDK.' }
    $inventory = Get-MsixPublishInventory $PublishDirectory
    $runtimeInfo = Assert-MsixPayload $inventory
    if ($ExpectedPayloadHash -and $ExpectedPayloadHash -ne $inventory.sha256) { throw 'The payload differs from the reviewed inventory hash.' }
    $logos = Get-MsixLogoInventory $LogoDirectory
    $manifest = New-MsixReviewManifest (Join-Path $PSScriptRoot 'Review.appxmanifest.xml') $Version $Publisher
    $destination = Assert-MsixRegularPath $DestinationRoot
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\','/')
    $private = Join-Path $repository '.agentstudio/tmp/packaging'
    if ($destination -eq $inventory.root -or $destination.StartsWith($inventory.root.TrimEnd('\','/') + '\',[StringComparison]::OrdinalIgnoreCase) -or
        $destination -eq $repository -or
        ($destination.StartsWith($repository + '\',[StringComparison]::OrdinalIgnoreCase) -and $destination -ne $private -and -not $destination.StartsWith($private + '\',[StringComparison]::OrdinalIgnoreCase))) {
        throw 'Use a private destination outside the payload and outside versioned source.'
    }
    if ($destination -ne $private -and -not $destination.StartsWith($private + '\',[StringComparison]::OrdinalIgnoreCase)) {
        for ($parent = $destination; $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
            if (Test-Path -LiteralPath (Join-Path $parent '.git')) { throw 'An external destination must be outside every source checkout.' }
        }
    }

    # Resolve only an installed SDK under the Windows known folder, never PATH, the project or a supplied executable.
    $sdk = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)) 'Windows Kits/10/bin'
    $sdk = Assert-MsixRegularPath $sdk
    if (-not [IO.Directory]::Exists($sdk)) { throw 'Windows SDK MakeAppx is missing; install/review it separately.' }
    $candidates = @(Get-ChildItem -LiteralPath $sdk -Directory | Where-Object { $_.Name -match '^10\.0\.[0-9]+\.0$' } | Sort-Object { [version]$_.Name } -Descending)
    $makeAppx = $null
    foreach ($candidate in $candidates) {
        $path = Assert-MsixRegularPath (Join-Path $candidate.FullName 'x64/makeappx.exe')
        if ([version]$candidate.Name -ge [version]'10.0.19041.0' -and [IO.File]::Exists($path)) { $makeAppx = $path; break }
    }
    if (-not $makeAppx) { throw 'An x64 Windows SDK MakeAppx version 10.0.19041.0 or later is required.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $makeAppx
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?i)(^|,\s*)O=Microsoft Corporation(,|$)') { throw 'The SDK MakeAppx signature is not trusted Microsoft code.' }
    $toolHash = (Get-FileHash -LiteralPath $makeAppx -Algorithm SHA256).Hash

    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $delivery = Join-Path $destination ('AgentStudio.Development-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $delivery) { throw 'The review delivery directory must be new.' }
    [IO.Directory]::CreateDirectory($delivery) | Out-Null
    $staging = Join-Path $delivery 'staging'
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    $result.payload = $inventory
    $result.runtimes = $runtimeInfo
    $result.logos = $logos
    $result.makeAppx = @{ path=$makeAppx; sha256=$toolHash; signature='Valid Microsoft' }
    $result.staging = $staging
    $copied = [Collections.Generic.List[object]]::new()
    foreach ($file in $inventory.files) {
        $target = Join-Path $staging $file.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy((Join-Path $inventory.root $file.path),$target,$false)
        $copied.Add(@{ path=$target; sha256=$file.sha256 })
    }
    [IO.Directory]::CreateDirectory((Join-Path $staging 'MsixAssets')) | Out-Null
    foreach ($logo in $logos) {
        $target = Join-Path $staging ('MsixAssets/' + $logo.name)
        [IO.File]::Copy($logo.source,$target,$false)
        $copied.Add(@{ path=$target; sha256=$logo.sha256 })
    }
    $manifestPath = Join-Path $staging 'AppxManifest.xml'
    $writerSettings = [Xml.XmlWriterSettings]::new()
    $writerSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $writerSettings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($manifestPath,$writerSettings)
    try { $manifest.Save($writer) } finally { $writer.Dispose() }
    $result.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $copied.Add(@{ path=$manifestPath; sha256=$result.manifestSha256 })
    if ((Get-MsixPublishInventory $inventory.root).sha256 -ne $inventory.sha256) { throw 'The source payload changed while staging.' }
    foreach ($file in $copied) { if ((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'A staged file differs from the reviewed input.' } }
    if ((Get-FileHash -LiteralPath $makeAppx -Algorithm SHA256).Hash -ne $toolHash) { throw 'The SDK tool changed before launch.' }

    $package = Join-Path $delivery ('AgentStudio.Development_' + $Version + '_x64.unsigned.msix')
    if (Test-Path -LiteralPath $package) { throw 'The output package must not already exist.' }
    $start = [Diagnostics.ProcessStartInfo]::new($makeAppx)
    $start.WorkingDirectory = $delivery
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('pack','/no','/h','SHA256','/d',$staging,'/p',$package)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit(600000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $result.makeAppxExitCode = $process.ExitCode
        [IO.File]::WriteAllText((Join-Path $delivery 'makeappx.log'),$stdout.GetAwaiter().GetResult() + [Environment]::NewLine + $stderr.GetAwaiter().GetResult())
        if ($timedOut) { throw 'MakeAppx exceeded ten minutes; staging and tool output are preserved.' }
        if ($process.ExitCode -ne 0 -or -not [IO.File]::Exists($package)) { throw 'MakeAppx failed; inspect its log and the preserved staging.' }
    }
    finally { $process.Dispose() }
    if ((Get-MsixPublishInventory $inventory.root).sha256 -ne $inventory.sha256) { throw 'The source payload changed during packaging.' }
    foreach ($file in $copied) { if ((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Staging changed during packaging.' } }
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        if ($archive.GetEntry('AppxSignature.p7x') -or -not $archive.GetEntry('AppxManifest.xml')) { throw 'The output is not the expected unsigned review package.' }
    }
    finally { $archive.Dispose() }
    $result.package = $package
    $result.packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.outcome = 'PackedUnsignedForHumanReview'
    $exitCode = 0
}
catch { $result.failure = $_.Exception.Message; [Console]::Error.WriteLine($_.Exception.Message) }
finally {
    # Never delete staging, replace an older delivery, sign, install or alter machine configuration.
    if ($delivery -and [IO.Directory]::Exists($delivery)) {
        $report = Join-Path $delivery 'review.json'
        $stream = [IO.FileStream]::new($report,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try { $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 12)); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    }
    Write-Output ($result | ConvertTo-Json -Depth 12)
}
exit $exitCode
