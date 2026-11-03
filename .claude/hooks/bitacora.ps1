# Daily prompt journal (UserPromptSubmit hook).
# Appends one line per prompt to .agentstudio/bitacora/YYYY-MM-DD.md: local time, short session id, tags and a
# one-line excerpt. It must never block or slow down a prompt: every failure is swallowed, the exit code is always 0
# and stdout stays empty (stdout of this hook would be injected into the model context).
# It never writes through a link: when .agentstudio or bitacora is a junction or a symbolic link, or the day file is a
# link or has a second name (a hard link to a file elsewhere), it writes nothing and prints one line to stderr.
# Claude Code runs it with pwsh when installed and falls back to Windows PowerShell 5.1: only APIs of both are used.

$ErrorActionPreference = 'Stop'

# Why $Path must not be written through, or $null when it is missing or is a plain folder (or a plain file) of one name.
function Get-LinkRefusal([string] $Path, [bool] $IsFile) {
    try { $attributes = [System.IO.File]::GetAttributes($Path) }
    catch [System.IO.FileNotFoundException], [System.IO.DirectoryNotFoundException] { return $null }

    # GetAttributes does not follow links: a junction or a symbolic link, dangling or not, shows its own attributes.
    if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) { return "'$Path' is a link" }
    $isDirectory = ($attributes -band [System.IO.FileAttributes]::Directory) -ne 0
    if ($IsFile -eq $isDirectory) { return "'$Path' is not a $(if ($IsFile) { 'file' } else { 'folder' })" }

    # LinkType is HardLink when the file has more than one name (GetFileInformationByHandle, in both PowerShells).
    if ($IsFile -and (Get-Item -LiteralPath $Path -Force).LinkType -eq 'HardLink') { return "'$Path' has more than one name" }
    return $null
}

function Get-FolderRefusal([string[]] $Folders) {
    foreach ($folder in $Folders) {
        $refusal = Get-LinkRefusal $folder $false
        if ($refusal) { return $refusal }
    }
    return $null
}

function Write-Refusal([string] $Reason) {
    # stderr of a hook that exits 0 is not shown to the model; it reaches the user's debug log.
    [Console]::Error.WriteLine("bitacora hook: nothing journaled, $Reason.")
}

try {
    # Claude Code sends UTF-8 JSON; the console default code page would mangle accents.
    [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
    $raw = [Console]::In.ReadToEnd()
    $data = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }

    $prompt = if ($data -and $data.prompt) { [string]$data.prompt } else { '' }

    # Subagent hand-backs and harness notifications arrive through the same event but are not prompts of the user.
    if ($prompt -match '^\s*(<agent-message|<task-notification|<system-reminder|\[SYSTEM NOTIFICATION)') { exit 0 }
    $session = if ($data -and $data.session_id) { ([string]$data.session_id).Substring(0, [Math]::Min(8, ([string]$data.session_id).Length)) } else { 'sin-id' }

    $root = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } elseif ($data -and $data.cwd) { [string]$data.cwd } else { (Get-Location).Path }

    # Both folders are children of the project folder: with no link among them the journal cannot resolve outside it.
    # Each is checked before anything is created in it, and again once created (a link planted meanwhile).
    $folders = @((Join-Path $root '.agentstudio'), (Join-Path (Join-Path $root '.agentstudio') 'bitacora'))
    foreach ($folder in $folders) {
        $refusal = Get-LinkRefusal $folder $false
        if (-not $refusal -and -not [System.IO.Directory]::Exists($folder)) {
            [System.IO.Directory]::CreateDirectory($folder) | Out-Null
            $refusal = Get-LinkRefusal $folder $false
        }
        if ($refusal) { Write-Refusal $refusal; exit 0 }
    }
    $dir = $folders[1]

    $now = Get-Date
    $file = Join-Path $dir ($now.ToString('yyyy-MM-dd') + '.md')
    $refusal = Get-LinkRefusal $file $true
    if ($refusal) { Write-Refusal $refusal; exit 0 }

    # Identification tags derived from keywords; always #prompt, #general when nothing matches.
    $rules = [ordered]@{
        'docs'          = 'doc(s|umenta)|readme|adr|informe'
        'tests'         = 'test|prueba'
        'git'           = 'commit|push|github|\bgit\b|rama|merge'
        'skills'        = 'skill'
        'bitacora'      = 'bit[aá]cora|registro diario'
        'juez'          = 'juez|debate|veredicto|auditor'
        'orquestacion'  = 'orquest|agente'
        'ui'            = '\bui\b|ux|wpf|xaml|pantalla|interfaz|ventana'
        'proveedores'   = 'proveedor|claude|codex|gemini'
        'seguridad'     = 'segur|credencial|secreto|permis'
        'bug'           = 'error|bug|fallo|falla'
    }
    $tags = @('#prompt')
    foreach ($tag in $rules.Keys) {
        if ($prompt -match "(?i)$($rules[$tag])") { $tags += "#$tag" }
    }
    $phase = [regex]::Match($prompt, '(?i)fase\s*(\d+)')
    if ($phase.Success) { $tags += "#fase-$($phase.Groups[1].Value)" }
    if ($tags.Count -eq 1) { $tags += '#general' }

    $excerpt = ($prompt -replace '\s+', ' ').Trim()
    if ($excerpt.Length -gt 160) { $excerpt = $excerpt.Substring(0, 157).TrimEnd() + '...' }
    if ($excerpt.Length -eq 0) { $excerpt = '(prompt vacío)' }
    $excerpt = $excerpt -replace '`', "'"

    $lines = @()
    if (-not (Test-Path -LiteralPath $file)) {
        $lines += "# Bitácora · $($now.ToString('yyyy-MM-dd'))"
        $lines += ''
        $lines += 'Registro automático de cada prompt (hook `UserPromptSubmit`): hora · sesión · tags · extracto.'
        $lines += 'Al cerrar cada petición, Claude añade debajo una línea «Hecho:» con el resultado.'
        $lines += ''
    }
    $lines += "- **$($now.ToString('HH:mm:ss'))** · sesión ``$session`` · $($tags -join ' ') · «$excerpt»"

    $text = ($lines -join [Environment]::NewLine) + [Environment]::NewLine
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($text)
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $stream = $null
        try {
            $stream = [System.IO.FileStream]::new($file, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)

            # Look again with the file open: while this handle lives its name can be neither removed nor replaced, so
            # the checks see the very file being written (a hard link swapped in before the open is caught here).
            $refusal = Get-FolderRefusal $folders
            if (-not $refusal) { $refusal = Get-LinkRefusal $file $true }
            if (-not $refusal) { $stream.Write($bytes, 0, $bytes.Length) }
            break
        }
        catch [System.IO.IOException] { Start-Sleep -Milliseconds 50 }
        finally { if ($null -ne $stream) { $stream.Dispose() } }
    }
    if ($refusal) { Write-Refusal $refusal }
}
catch {
    # Never block the prompt.
}
exit 0
