# Validation only: importing this module never launches a program or changes a file.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-MsixRegularPath([string] $Path) {
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if ($full.StartsWith('\\')) { throw 'Use local packaging paths, not a network share.' }
    if ($full -match '(?i)(^|[\\/])\.(?:claude|codex|gemini|agents)([\\/]|$)') { throw 'Provider profiles are never packaging inputs or outputs.' }
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if (Test-Path -LiteralPath $part) {
            $item = Get-Item -LiteralPath $part -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.LinkType -eq 'HardLink') {
                throw 'Packaging paths must not contain links.'
            }
        }
    }
    return $full
}

function Get-MsixSha256([byte[]] $Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Get-MsixPublishInventory([string] $PublishDirectory) {
    $root = Assert-MsixRegularPath $PublishDirectory
    if ($root -match '(?i)(^|[\\/])\.(?:claude|codex|gemini|agents)([\\/]|$)' -or -not [IO.Directory]::Exists($root)) {
        throw 'Use an existing reviewed publish directory outside provider profiles.'
    }
    $files = [Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    $total = 0L
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\','/')
            if ($relative -match '(?i)(^|/)(?:\.[^/]+|cache|caches|logs?|profiles?|worktrees?|bitacora|models|sessions|node_modules|obj|TestResults)(/|$)' -or
                $item.Name -match '(?i)^(?:secrets?|credentials?)(?:\.|$)' -or
                ($relative -notlike '*/*' -and $item.Name -match '(?i)^tokens?(?:\.|$)') -or
                $item.Extension -match '(?i)^\.(?:pfx|p12|pem|key|db|sqlite|log|trx|patch|zip|msix|appx|p7x|ps1|cmd|bat|sh|cs|csproj|sln|slnx|props|targets|onnx|model|safetensors|gguf)$' -or
                $relative -match '(?i)^(?:AppxManifest\.xml|AppxBlockMap\.xml|AppxSignature\.p7x|\[Content_Types\]\.xml|MsixAssets)(/|$)') {
                throw 'The publish directory contains development, private or previous packaging material.'
            }
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.LinkType -eq 'HardLink') { throw 'The payload contains a link.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            if ($item.Extension -eq '.exe' -and $relative -notin @('AgentStudio.Desktop.exe','AgentStudio.SkillsMcp.exe','AgentStudio.GraphWorker.exe')) { throw 'Only the three reviewed apphosts belong in this payload; do not bundle provider or system tools.' }
            if (-not $names.Add($relative)) { throw 'Payload paths differ only by letter case.' }
            $total += $item.Length
            if ($names.Count -gt 20000 -or $total -gt 2147483648) { throw 'The publish directory exceeds the packaging review limits.' }
            $files.Add($relative, [ordered]@{ path=$relative; length=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
        }
    }
    $records = @($files.Values | ForEach-Object { $_ })
    $lines = @($records | ForEach-Object { $_.path + "`0" + $_.length.ToString([Globalization.CultureInfo]::InvariantCulture) + "`0" + $_.sha256 })
    return [pscustomobject]@{ root=$root; files=$records; bytes=$total; sha256=(Get-MsixSha256 ([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))) }
}

function Assert-MsixX64Pe([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw 'A required native component is not a PE binary.' }
        $stream.Position = 60
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt ($stream.Length - 6)) { throw 'A required native component has an invalid PE header.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'This review package requires x64 native components.' }
    }
    finally { $reader.Dispose() }
}

function Assert-MsixPayload($Inventory) {
    $known = @{}
    foreach ($file in $Inventory.files) { $known[$file.path] = $true }
    $runtimes = @()
    foreach ($app in @('AgentStudio.Desktop','AgentStudio.SkillsMcp','AgentStudio.GraphWorker')) {
        foreach ($extension in @('.exe','.dll','.deps.json','.runtimeconfig.json')) {
            if (-not $known.ContainsKey($app + $extension)) { throw ('A required published app component is missing: ' + $app + $extension) }
        }
        if ((Get-Item -LiteralPath (Join-Path $Inventory.root ($app + '.deps.json'))).Length -gt 16777216) { throw 'A dependency manifest exceeds 16 MiB.' }
        Assert-MsixX64Pe (Join-Path $Inventory.root ($app + '.exe'))
        $configPath = Join-Path $Inventory.root ($app + '.runtimeconfig.json')
        if ((Get-Item -LiteralPath $configPath).Length -gt 1048576) { throw 'A runtime configuration exceeds 1 MiB.' }
        $options = ([IO.File]::ReadAllText($configPath) | ConvertFrom-Json -AsHashtable).runtimeOptions
        if ($options.tfm -notmatch '^net10\.0(?:-|$)') { throw 'The reviewed app targets .NET 10.' }
        $frameworks = @()
        if ($options.ContainsKey('framework')) { $frameworks += $options.framework }
        if ($options.ContainsKey('frameworks')) { $frameworks += $options.frameworks }
        $included = @()
        if ($options.ContainsKey('includedFrameworks')) { $included += $options.includedFrameworks }
        if (($frameworks.Count -eq 0) -eq ($included.Count -eq 0)) { throw 'The runtime deployment mode is missing or ambiguous.' }
        $declared = @($frameworks) + @($included)
        foreach ($framework in $declared) {
            if ($framework.name -notin @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') -or $framework.version -notmatch '^10\.[0-9]+\.[0-9]+$') { throw 'An unexpected runtime dependency requires separate review.' }
        }
        if ($app -eq 'AgentStudio.Desktop' -and 'Microsoft.WindowsDesktop.App' -notin @($declared | ForEach-Object { $_.name })) { throw 'The desktop runtime dependency is missing.' }
        if ($included.Count) {
            foreach ($native in @('coreclr.dll','hostfxr.dll','hostpolicy.dll')) {
                if (-not $known.ContainsKey($native)) { throw 'The self-contained runtime is incomplete.' }
                Assert-MsixX64Pe (Join-Path $Inventory.root $native)
            }
        }
        $runtimes += @{ app=$app; mode=$(if ($included.Count) { 'SelfContained' } else { 'FrameworkDependent' }); frameworks=@($frameworks); includedFrameworks=@($included) }
    }
    foreach ($asset in @('Assets/editor/editor.html','Assets/editor/editor.js','Assets/monaco/vs/loader.js','Assets/terminal/index.html','Assets/terminal/terminal.js','Assets/terminal/terminal.css','Assets/terminal/xterm/xterm.js','Assets/terminal/xterm/xterm.css')) {
        if (-not $known.ContainsKey($asset)) { throw ('A required published editor/terminal asset is missing: ' + $asset) }
    }
    foreach ($native in @('e_sqlite3.dll','onnxruntime.dll','WebView2Loader.dll')) {
        if (-not $known.ContainsKey($native) -and -not $known.ContainsKey('runtimes/win-x64/native/' + $native)) { throw ('A required x64 native dependency is missing: ' + $native) }
        $nativePath = if ($known.ContainsKey($native)) { $native } else { 'runtimes/win-x64/native/' + $native }
        Assert-MsixX64Pe (Join-Path $Inventory.root $nativePath)
    }
    $deps = [IO.File]::ReadAllText((Join-Path $Inventory.root 'AgentStudio.Desktop.deps.json')) | ConvertFrom-Json -AsHashtable
    if ($deps.runtimeTarget.name -notmatch '/win-x64$') { throw 'Use the reviewed win-x64 publish output, not a portable build folder.' }
    return ,$runtimes
}

function Get-MsixLogoInventory([string] $LogoDirectory) {
    $root = Assert-MsixRegularPath $LogoDirectory
    try { $null = [Reflection.Assembly]::Load('System.Drawing.Common') } catch { throw 'The Windows PNG decoder is unavailable in this PowerShell installation.' }
    $logos = @()
    foreach ($definition in @(@('StoreLogo.png',50),@('Square44x44Logo.png',44),@('Square150x150Logo.png',150))) {
        $path = Assert-MsixRegularPath (Join-Path $root $definition[0])
        if (-not [IO.File]::Exists($path) -or (Get-Item -LiteralPath $path).Length -gt 1048576) { throw 'A reviewed PNG logo is missing or exceeds 1 MiB.' }
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 33 -or [Convert]::ToHexString($bytes,0,8) -ne '89504E470D0A1A0A' -or [Text.Encoding]::ASCII.GetString($bytes,12,4) -ne 'IHDR') { throw 'A logo has no valid PNG header.' }
        $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes,16))
        $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes,20))
        if ($width -ne $definition[1] -or $height -ne $definition[1]) { throw 'Reviewed logos must be exactly 50, 44 and 150 pixels square respectively.' }
        $pngStream = [IO.MemoryStream]::new($bytes,$false)
        $image = $null
        try {
            $image = [Drawing.Image]::FromStream($pngStream,$false,$true)
            if ($image.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid -or $image.Width -ne $width -or $image.Height -ne $height) { throw 'A logo is not a decodable PNG of the declared size.' }
        }
        finally { if ($image) { $image.Dispose() }; $pngStream.Dispose() }
        $logos += @{ name=$definition[0]; source=$path; sha256=(Get-MsixSha256 $bytes) }
    }
    return ,$logos
}

function Assert-MsixElementShape([Xml.XmlElement] $Element, [string] $Name, [string] $Namespace, [string[]] $Attributes) {
    if ($null -eq $Element -or $Element.PSBase.LocalName -cne $Name -or $Element.PSBase.NamespaceURI -cne $Namespace) { throw 'A reviewed manifest element or namespace changed.' }
    foreach ($attribute in $Element.PSBase.Attributes) {
        if ($attribute.NamespaceURI -ceq 'http://www.w3.org/2000/xmlns/') { continue }
        if ($attribute.NamespaceURI -cne '' -or $attribute.LocalName -cnotin $Attributes) { throw 'A reviewed manifest element has an extra attribute.' }
    }
    foreach ($attribute in $Attributes) { if (-not $Element.HasAttribute($attribute)) { throw 'A required manifest attribute is missing.' } }
}

function Get-MsixClosedChildren([Xml.XmlElement] $Parent, [string] $Namespace, [string[]] $Names) {
    $children = @{}
    foreach ($child in $Parent.PSBase.ChildNodes) {
        if ($child -isnot [Xml.XmlElement]) { continue }
        if ($child.PSBase.NamespaceURI -cne $Namespace -or $child.PSBase.LocalName -cnotin $Names -or $children.ContainsKey($child.PSBase.LocalName)) { throw 'A reviewed manifest section has extra, duplicate or foreign elements.' }
        $children[$child.PSBase.LocalName] = $child
    }
    if ($children.Count -ne $Names.Count) { throw 'A required manifest element is missing.' }
    return ,$children
}

function Assert-MsixLeaf([Xml.XmlElement] $Element) {
    if ($Element.SelectSingleNode('*')) { throw 'A reviewed manifest leaf contains extra elements.' }
}

function New-MsixReviewManifest([string] $TemplatePath, [string] $Version, [string] $Publisher) {
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' -or @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count) { throw 'MSIX requires four version components, each from 0 to 65535.' }
    if ([string]::IsNullOrWhiteSpace($Publisher) -or $Publisher.Length -gt 8192 -or $Publisher -match '[\x00-\x1F]') { throw 'An explicit certificate publisher distinguished name is required.' }
    $null = [Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Publisher)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $reader = [Xml.XmlReader]::Create($TemplatePath,$settings)
    try { $document.Load($reader) } finally { $reader.Dispose() }
    $foundation = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
    $uap = 'http://schemas.microsoft.com/appx/manifest/uap/windows10'
    $restricted = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities'
    # Prefixes are aliases. Compare namespace URI and local name, never a qualified spelling such as 'Extensions'.
    if ($document.SelectNodes("//*[local-name()='Extensions']").Count) { throw 'Extensions are not part of the reviewed package.' }
    Assert-MsixElementShape ($document.PSBase.DocumentElement) 'Package' $foundation @('IgnorableNamespaces')
    $sections = Get-MsixClosedChildren ($document.PSBase.DocumentElement) $foundation @('Identity','Properties','Dependencies','Resources','Applications','Capabilities')
    $identity = $sections['Identity']
    Assert-MsixElementShape $identity 'Identity' $foundation @('Name','Publisher','Version','ProcessorArchitecture')
    Assert-MsixLeaf $identity
    if ($identity.GetAttribute('Name') -cne 'AgentStudio.Development' -or $identity.GetAttribute('ProcessorArchitecture') -cne 'x64') { throw 'The reviewed development identity changed.' }

    Assert-MsixElementShape ($sections['Properties']) 'Properties' $foundation @()
    $properties = Get-MsixClosedChildren ($sections['Properties']) $foundation @('DisplayName','PublisherDisplayName','Description','Logo')
    foreach ($name in $properties.Keys) {
        Assert-MsixElementShape ($properties[$name]) $name $foundation @()
        Assert-MsixLeaf ($properties[$name])
    }
    if ($properties['Logo'].PSBase.InnerText -cne 'MsixAssets\StoreLogo.png') { throw 'The reviewed package logo path changed.' }

    Assert-MsixElementShape ($sections['Dependencies']) 'Dependencies' $foundation @()
    $dependencies = Get-MsixClosedChildren ($sections['Dependencies']) $foundation @('TargetDeviceFamily')
    $device = $dependencies['TargetDeviceFamily']
    Assert-MsixElementShape $device 'TargetDeviceFamily' $foundation @('Name','MinVersion','MaxVersionTested')
    Assert-MsixLeaf $device
    if ($device.GetAttribute('Name') -cne 'Windows.Desktop' -or $device.GetAttribute('MinVersion') -cne '10.0.22000.0' -or $device.GetAttribute('MaxVersionTested') -cne '10.0.22000.0') { throw 'The reviewed Windows 11 desktop target changed.' }

    Assert-MsixElementShape ($sections['Resources']) 'Resources' $foundation @()
    $resources = @($sections['Resources'].PSBase.ChildNodes | Where-Object { $_ -is [Xml.XmlElement] })
    $languages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($resources.Count -ne 2) { throw 'The reviewed package languages changed.' }
    foreach ($resource in $resources) {
        Assert-MsixElementShape $resource 'Resource' $foundation @('Language')
        Assert-MsixLeaf $resource
        $language = $resource.GetAttribute('Language')
        if ($language -notin @('es-ES','en-US') -or -not $languages.Add($language)) { throw 'The reviewed package languages changed.' }
    }

    Assert-MsixElementShape ($sections['Capabilities']) 'Capabilities' $foundation @()
    $capabilities = Get-MsixClosedChildren ($sections['Capabilities']) $restricted @('Capability')
    $capability = $capabilities['Capability']
    Assert-MsixElementShape $capability 'Capability' $restricted @('Name')
    Assert-MsixLeaf $capability
    if ($capability.GetAttribute('Name') -cne 'runFullTrust') { throw 'The reviewed capability set changed.' }

    Assert-MsixElementShape ($sections['Applications']) 'Applications' $foundation @()
    $applications = Get-MsixClosedChildren ($sections['Applications']) $foundation @('Application')
    $application = $applications['Application']
    Assert-MsixElementShape $application 'Application' $foundation @('Id','Executable','EntryPoint')
    if ($application.GetAttribute('Id') -cne 'AgentStudio' -or $application.GetAttribute('Executable') -cne 'AgentStudio.Desktop.exe' -or $application.GetAttribute('EntryPoint') -cne 'Windows.FullTrustApplication') { throw 'The reviewed entry point changed.' }
    $visuals = Get-MsixClosedChildren $application $uap @('VisualElements')
    $visual = $visuals['VisualElements']
    Assert-MsixElementShape $visual 'VisualElements' $uap @('DisplayName','Description','Square150x150Logo','Square44x44Logo','BackgroundColor')
    Assert-MsixLeaf $visual
    if ($visual.GetAttribute('Square150x150Logo') -cne 'MsixAssets\Square150x150Logo.png' -or $visual.GetAttribute('Square44x44Logo') -cne 'MsixAssets\Square44x44Logo.png') { throw 'The reviewed tile logo paths changed.' }
    $identity.SetAttribute('Version',$Version)
    $identity.SetAttribute('Publisher',$Publisher)
    return ,$document
}

Export-ModuleMember -Function Assert-MsixRegularPath,Get-MsixPublishInventory,Assert-MsixPayload,Get-MsixLogoInventory,New-MsixReviewManifest
