#requires -Version 7.2
# Synthetic validation fixtures only. No MakeAppx, real app, provider, certificate, signing or installation is invoked.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReviewMsix.Validation.psm1') -Force
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('as-msix-validation-fixture-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$publish = Join-Path $fixtureRoot 'synthetic-publish'
[IO.Directory]::CreateDirectory($publish) | Out-Null
$checks = 0
function Assert-Check([bool] $Condition) { if (-not $Condition) { throw 'A packaging validation check failed.' }; $script:checks++ }
function Assert-Rejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Check $rejected
}
function Write-Fixture([string] $Relative, [string] $Text = 'Synthetic fixture, never product payload.') {
    $path = Join-Path $publish $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path,$Text)
}
# Only the header is a synthetic PE fixture. These files are never executed or passed to MakeAppx.
$pe = [byte[]]::new(70)
[Array]::Copy([BitConverter]::GetBytes([uint16]0x5A4D),0,$pe,0,2)
[Array]::Copy([BitConverter]::GetBytes([int]64),0,$pe,60,4)
[Array]::Copy([BitConverter]::GetBytes([uint32]0x00004550),0,$pe,64,4)
[Array]::Copy([BitConverter]::GetBytes([uint16]0x8664),0,$pe,68,2)
foreach ($app in @('AgentStudio.Desktop','AgentStudio.SkillsMcp','AgentStudio.GraphWorker')) {
    [IO.File]::WriteAllBytes((Join-Path $publish ($app + '.exe')),$pe)
    Write-Fixture ($app + '.dll')
    Write-Fixture ($app + '.deps.json') '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"}}'
    $framework = if ($app -eq 'AgentStudio.Desktop') { 'Microsoft.WindowsDesktop.App' } else { 'Microsoft.NETCore.App' }
    Write-Fixture ($app + '.runtimeconfig.json') (@{ runtimeOptions=@{ tfm='net10.0'; framework=@{ name=$framework; version='10.0.0' } } } | ConvertTo-Json -Depth 5)
}
foreach ($asset in @('Assets/editor/editor.html','Assets/editor/editor.js','Assets/monaco/vs/loader.js','Assets/terminal/index.html','Assets/terminal/terminal.js','Assets/terminal/terminal.css','Assets/terminal/xterm/xterm.js','Assets/terminal/xterm/xterm.css')) { Write-Fixture $asset }
foreach ($native in @('e_sqlite3.dll','onnxruntime.dll','WebView2Loader.dll')) { [IO.File]::WriteAllBytes((Join-Path $publish $native),$pe) }
$initial = Get-MsixPublishInventory $publish
Assert-Check ((Assert-MsixPayload $initial).Count -eq 3)
Assert-Check ((Get-MsixPublishInventory $publish).sha256 -eq $initial.sha256)
Write-Fixture 'Assets/editor/editor.js' 'Changed synthetic bytes.'
Assert-Check ((Get-MsixPublishInventory $publish).sha256 -ne $initial.sha256)
Write-Fixture 'Assets/editor/editor.js'
foreach ($forbidden in @('.env','credentials.json','AppxManifest.xml','old.log','source.cs','private.pfx','claude.exe')) {
    Write-Fixture $forbidden
    Assert-Rejected { Get-MsixPublishInventory $publish }
    [IO.File]::Delete((Join-Path $publish $forbidden))
}
$privateFolder = Join-Path $publish '.agentstudio'
[IO.Directory]::CreateDirectory($privateFolder) | Out-Null
Assert-Rejected { Get-MsixPublishInventory $publish }
[IO.Directory]::Delete($privateFolder)
Assert-Rejected { Get-MsixPublishInventory (Join-Path $fixtureRoot '.codex') }
$worker = Join-Path $publish 'AgentStudio.GraphWorker.exe'
[IO.File]::Delete($worker)
Assert-Rejected { Assert-MsixPayload (Get-MsixPublishInventory $publish) }
[IO.File]::WriteAllBytes($worker,$pe)
$pe[68] = 0x4C; $pe[69] = 0x01
[IO.File]::WriteAllBytes($worker,$pe)
Assert-Rejected { Assert-MsixPayload (Get-MsixPublishInventory $publish) }
$logos = Join-Path $fixtureRoot 'SYNTHETIC-LOGOS-NOT-BRANDING'
Assert-Rejected { Get-MsixLogoInventory $logos }
[IO.Directory]::CreateDirectory($logos) | Out-Null
$null = [Reflection.Assembly]::Load('System.Drawing.Common')
foreach ($definition in @(@('StoreLogo.png',50),@('Square44x44Logo.png',44),@('Square150x150Logo.png',150))) {
    $bitmap = [Drawing.Bitmap]::new($definition[1],$definition[1])
    try { $bitmap.Save((Join-Path $logos $definition[0]),[Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
Assert-Check ((Get-MsixLogoInventory $logos).Count -eq 3)
[IO.File]::WriteAllText((Join-Path $logos 'StoreLogo.png'),'Not a PNG.')
Assert-Rejected { Get-MsixLogoInventory $logos }
$template = Join-Path $PSScriptRoot 'Review.appxmanifest.xml'
$manifest = New-MsixReviewManifest $template '1.2.3.4' 'CN=SYNTHETIC VALIDATION FIXTURE'
Assert-Check ($manifest.DocumentElement.Identity.GetAttribute('Version') -eq '1.2.3.4')
Assert-Check ($manifest.DocumentElement.Identity.GetAttribute('Publisher') -eq 'CN=SYNTHETIC VALIDATION FIXTURE')
Assert-Rejected { New-MsixReviewManifest $template '1.2.3' 'CN=FIXTURE' }
Assert-Rejected { New-MsixReviewManifest $template '1.2.3.65536' 'CN=FIXTURE' }
$altered = Join-Path $fixtureRoot 'synthetic-extra-capability.xml'
[IO.File]::WriteAllText($altered,([IO.File]::ReadAllText($template).Replace('<Capabilities>','<Capabilities><Capability Name="internetClient" />')))
Assert-Rejected { New-MsixReviewManifest $altered '1.2.3.4' 'CN=FIXTURE' }
# Prefix spelling cannot weaken the closed namespace/shape contract.
$foundation = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
$restricted = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities'
$originalManifest = [IO.File]::ReadAllText($template)
$mutations = [ordered]@{
    'lowered-minimum' = $originalManifest.Replace('MinVersion="10.0.22000.0"','MinVersion="10.0.19041.0"')
    'changed-tested-version' = $originalManifest.Replace('MaxVersionTested="10.0.22000.0"','MaxVersionTested="10.0.19041.0"')
    'different-device-family' = $originalManifest.Replace('Name="Windows.Desktop"','Name="Windows.Universal"')
    'foreign-device-family' = $originalManifest.Replace('<TargetDeviceFamily ','<TargetDeviceFamily xmlns="urn:synthetic-foreign" ')
    'prefixed-extensions' = $originalManifest.Replace('</Application>',('<f:Extensions xmlns:f="' + $foundation + '" /></Application>'))
    'prefixed-foreign-extensions' = $originalManifest.Replace('</Application>','<f:Extensions xmlns:f="urn:synthetic-foreign" /></Application>')
    'foreign-capability' = $originalManifest.Replace('<rescap:Capability ','<x:Capability xmlns:x="urn:synthetic-foreign" ')
    'capability-lookalike' = $originalManifest.Replace('<rescap:Capability ','<rescap:Other ')
    'extra-capability-attribute' = $originalManifest.Replace('Name="runFullTrust"','Name="runFullTrust" Extra="synthetic"')
    'duplicate-capabilities-section' = $originalManifest.Replace('</Capabilities>','</Capabilities><Capabilities />')
    'extra-application' = $originalManifest.Replace('</Applications>','<Application Id="Extra" Executable="AgentStudio.Desktop.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>')
    'foreign-application' = $originalManifest.Replace('<Application Id=','<x:Application xmlns:x="urn:synthetic-foreign" Id=').Replace('</Application>','</x:Application>')
    'extra-application-attribute' = $originalManifest.Replace('Id="AgentStudio" Executable=','Id="AgentStudio" StartPage="synthetic.html" Executable=')
}
foreach ($mutation in $mutations.GetEnumerator()) {
    $mutationPath = Join-Path $fixtureRoot ('synthetic-' + $mutation.Key + '.xml')
    [IO.File]::WriteAllText($mutationPath,$mutation.Value)
    Assert-Rejected { New-MsixReviewManifest $mutationPath '1.2.3.4' 'CN=FIXTURE' }
}
# Equivalent prefixes with the same namespace URI remain legitimate.
$prefixedPath = Join-Path $fixtureRoot 'synthetic-equivalent-prefixes.xml'
$prefixedManifest = $originalManifest.Replace('<Package xmlns=',('<f:Package xmlns:f="' + $foundation + '" xmlns='))
$prefixedManifest = $prefixedManifest.Replace('</Package>','</f:Package>').Replace('<Application Id=','<f:Application Id=').Replace('</Application>','</f:Application>')
$prefixedManifest = $prefixedManifest.Replace('<rescap:Capability ',('<r:Capability xmlns:r="' + $restricted + '" '))
[IO.File]::WriteAllText($prefixedPath,$prefixedManifest)
$equivalent = New-MsixReviewManifest $prefixedPath '1.2.3.4' 'CN=FIXTURE'
Assert-Check ($equivalent.DocumentElement.Identity.GetAttribute('Publisher') -eq 'CN=FIXTURE')
# Keep fixtures for inspection. Their files cannot be confused with a packaged/signed/installed product.
[pscustomobject]@{ outcome='ValidationChecksPassed'; checks=$checks; syntheticFixtureRoot=$fixtureRoot; packaged=$false; installed=$false }
