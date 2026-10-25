# Shared helpers for the KH 1.5+2.5 Hotfix scripts. Compatible with Windows PowerShell 5.1 and PowerShell 7.

$script:AppId              = '2552430'
$script:HotfixRoot         = $PSScriptRoot
$script:Cli                = Join-Path $PSScriptRoot 'KHHotfixCli.exe'
$script:Wrapper            = Join-Path $PSScriptRoot 'KHHotfix.exe'
$script:StateDir           = Join-Path $PSScriptRoot 'State'
$script:StateFile          = Join-Path $script:StateDir 'install.json'
$script:ManagedDir         = Join-Path $env:LOCALAPPDATA 'KH-Hotfix'
$script:ShortcutFolderName = 'KH Hotfix'
$script:GameExes           = @('KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe', 'KINGDOM HEARTS HD 1.5+2.5 Launcher.exe', 'KINGDOM HEARTS FINAL MIX.exe',
                               'KINGDOM HEARTS Re_Chain of Memories.exe', 'KINGDOM HEARTS II FINAL MIX.exe', 'KINGDOM HEARTS Birth by Sleep FINAL MIX.exe',
                               'KINGDOM HEARTS Theater.exe', 'WaitTitleProject.exe')
$script:SteamId64Base      = [int64]76561197960265728
$script:AnyWrapperPattern  = '"[^"]*\\KHHotfix\.exe"\s+%command%'   # the hotfix in a launch option, from any folder
$script:YesPattern         = '^(y|yes|j|ja)$'

function Write-Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "   [OK]   $text" -ForegroundColor Green }
function Write-Warn2([string]$text){ Write-Host "   [WARN] $text" -ForegroundColor Yellow }
function Write-Info([string]$text) { Write-Host "          $text" }

function Test-IsAdmin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# ---- install state ------------------------------------------------------------------------------
function Get-HotfixState {
    $s = $null
    if (Test-Path -LiteralPath $script:StateFile) { $s = Get-Content -Raw -LiteralPath $script:StateFile | ConvertFrom-Json }
    if (-not $s) { $s = New-Object psobject }
    foreach ($p in 'GpuPrefs', 'LaunchOption', 'InstalledAt', 'Shortcuts', 'Managed') {
        if (@($s.PSObject.Properties | ForEach-Object { $_.Name }) -notcontains $p) { $s | Add-Member -NotePropertyName $p -NotePropertyValue $null }
    }
    if ($null -eq $s.GpuPrefs) { $s.GpuPrefs = @() }
    $s
}

function Save-HotfixState($state) {
    New-Item -ItemType Directory -Force -Path $script:StateDir | Out-Null
    $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $script:StateFile -Encoding UTF8
}

# ---- hotfix binaries ----------------------------------------------------------------------------
function Invoke-Cli([string[]]$arguments) {
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = (& $script:Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join [Environment]::NewLine
        $code = $LASTEXITCODE
    }
    catch { throw "Windows could not run $([IO.Path]::GetFileName($script:Cli)): $($_.Exception.Message)" }
    finally { $ErrorActionPreference = $eap }
    [pscustomobject]@{ Code = $code; Output = "$out".TrimEnd() }
}

# Compiles src\*.cs with the C# compiler that ships with the .NET Framework 4.x on every Windows 10/11 PC.
function Invoke-HotfixBuild([string]$root, [string]$outDir) {
    if (-not $outDir) { $outDir = $root }
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) { throw "The C# compiler of the .NET Framework 4 was not found: $csc" }
    $src = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName })
    if ($src.Count -eq 0) { throw "No source files found in $(Join-Path $root 'src')" }
    $opts = @('/nologo', '/optimize+', '/platform:x64', '/codepage:65001', '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
    $builds = @(@{ Out = 'KHHotfix.exe'; Extra = @('/target:winexe') }, @{ Out = 'KHHotfixCli.exe'; Extra = @('/target:exe', '/define:CLI') })
    foreach ($b in $builds) {
        $eap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $log = (& $csc @opts $b.Extra ('/out:' + (Join-Path $outDir $b.Out)) $src 2>&1 | ForEach-Object { "$_" }) -join [Environment]::NewLine
            $code = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $eap }
        if ($code -ne 0) { throw "Building $($b.Out) failed:`n$log" }
    }
}

function Get-RunningHotfix([string]$root) {
    $prefix = $root.TrimEnd('\') + '\'
    @(Get-Process -Name KHHotfix -ErrorAction SilentlyContinue | Where-Object {
        $p = $null
        try { $p = $_.Path } catch { }
        $p -and $p.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    })
}

function Get-SmartAppControlState {
    $v = [Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CI\Policy', 'VerifiedAndReputablePolicyState', $null)
    switch ($v) { 0 { 'off' } 1 { 'on' } 2 { 'evaluation' } default { 'not available' } }
}

# ---- files ----------------------------------------------------------------------------------------
# Removes the "downloaded from the internet" mark so Windows does not warn every time a script is opened.
function Unblock-HotfixFiles([string]$root) {
    $n = 0
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue) {
        if ($f.FullName -match '\\(Backups|Logs|Reports|CrashDumps|\.git)\\') { continue }
        if (Get-Item -LiteralPath $f.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue) { Unblock-File -LiteralPath $f.FullName; $n++ }
    }
    $n
}

# Copies the program files (not the personal data folders, not the user's KHHotfix.ini). Returns $true if the ini was new.
function Copy-HotfixFiles([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    & robocopy.exe $from.TrimEnd('\') $to.TrimEnd('\') /E /XD Backups Logs Reports State CrashDumps .git .github tests /XF *.exe KHHotfix.ini *.khhotfix-tmp /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $code = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($code -ge 8) { throw "Copying the hotfix to $to failed (robocopy exit code $code)" }
    $ini = Join-Path $to 'KHHotfix.ini'
    if (Test-Path -LiteralPath $ini) { return $false }
    Copy-Item -LiteralPath (Join-Path $from 'KHHotfix.ini') -Destination $ini
    $true
}

# Deletes the hotfix folder once the window that started the uninstaller has closed.
function Remove-HotfixFolderLater([string]$root) {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
    $q = $root.Replace("'", "''")
    $cmd = "Wait-Process -Id $parent -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; for (`$i = 0; `$i -lt 30 -and (Test-Path -LiteralPath '$q'); `$i++) { Remove-Item -LiteralPath '$q' -Recurse -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }"
    Start-Process -FilePath powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd) -WindowStyle Hidden
}

# ---- Start menu ---------------------------------------------------------------------------------
function New-HotfixShortcuts([string]$root, [string]$gameDir, [string]$programsDir) {
    $dir = Join-Path $programsDir $script:ShortcutFolderName
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $kh = Join-Path $gameDir 'KINGDOM HEARTS HD 1.5+2.5 Launcher.exe'
    $shell32 = Join-Path $env:WINDIR 'System32\shell32.dll'
    $items = @(
        @{ Name = 'Restore a KINGDOM HEARTS save'; Target = (Join-Path $root 'Restore.cmd'); Args = ''; Icon = $kh }
        @{ Name = 'KH Hotfix diagnostics'; Target = (Join-Path $root 'Diagnose.cmd'); Args = ''; Icon = "$shell32,23" }
        @{ Name = 'Uninstall KH Hotfix'; Target = (Join-Path $root 'Uninstall.cmd'); Args = ''; Icon = "$shell32,31" }
        @{ Name = 'KH Hotfix folder'; Target = (Join-Path $env:WINDIR 'explorer.exe'); Args = ('"' + $root + '"'); Icon = "$shell32,3" }
    )
    $ws = New-Object -ComObject WScript.Shell
    try {
        foreach ($i in $items) {
            $l = $ws.CreateShortcut((Join-Path $dir ($i.Name + '.lnk')))
            $l.TargetPath = $i.Target
            $l.Arguments = $i.Args
            $l.WorkingDirectory = $root
            if (Test-Path -LiteralPath ($i.Icon -replace ',\d+$', '')) { $l.IconLocation = $(if ($i.Icon -match ',\d+$') { $i.Icon } else { "$($i.Icon),0" }) }
            $l.Save()
        }
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($ws) }
    $dir
}

function Remove-HotfixShortcuts([string]$dir) {
    if ($dir -and (Split-Path -Leaf $dir) -eq $script:ShortcutFolderName -and (Test-Path -LiteralPath $dir)) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

# ---- Steam ----------------------------------------------------------------------------------------
function Get-SteamPath {
    $p = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($p) { return ($p -replace '/', '\') }
    'C:\Program Files (x86)\Steam'
}

function Get-SteamLibraries {
    $steam = Get-SteamPath
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -Raw -LiteralPath $vdf), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
    }
    $libs | Select-Object -Unique
}

function Find-KHInstall {
    foreach ($lib in Get-SteamLibraries) {
        $acf = Join-Path $lib "steamapps\appmanifest_$($script:AppId).acf"
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $t = Get-Content -Raw -LiteralPath $acf
        $dir = [regex]::Match($t, '"installdir"\s+"([^"]+)"').Groups[1].Value
        $path = Join-Path $lib "steamapps\common\$dir"
        if (Test-Path -LiteralPath $path) {
            return [pscustomobject]@{
                Path = $path; Manifest = $acf
                BuildId = [regex]::Match($t, '"buildid"\s+"(\d+)"').Groups[1].Value
                LastOwner = [regex]::Match($t, '"LastOwner"\s+"(\d+)"').Groups[1].Value
            }
        }
    }
    $null
}

function Get-LocalConfigPath([int64]$accountId) { Join-Path (Get-SteamPath) "userdata\$accountId\config\localconfig.vdf" }

# Chooses the Steam account from loginusers.vdf: "MostRecent" (older Steam), the auto-login account, then the newest timestamp.
function Select-SteamAccount([string]$loginUsersText, [string]$autoLoginUser, [scriptblock]$hasConfig) {
    $users = @(foreach ($m in [regex]::Matches($loginUsersText, '"(\d{17})"\s*\{([^}]*)\}')) {
        $b = $m.Groups[2].Value
        [pscustomobject]@{
            Id         = [int64]$m.Groups[1].Value - $script:SteamId64Base
            Name       = [regex]::Match($b, '(?i)"AccountName"\s+"([^"]*)"').Groups[1].Value
            MostRecent = [regex]::IsMatch($b, '(?i)"MostRecent"\s+"1"')
            Timestamp  = [int64]('0' + [regex]::Match($b, '(?i)"Timestamp"\s+"(\d+)"').Groups[1].Value)
        }
    })
    if ($hasConfig) {
        $withConfig = @($users | Where-Object { & $hasConfig $_.Id })
        if ($withConfig.Count -gt 0) { $users = $withConfig }
    }
    if ($users.Count -eq 0) { return $null }
    $pick = $users | Where-Object { $_.MostRecent } | Select-Object -First 1
    if (-not $pick -and $autoLoginUser) { $pick = $users | Where-Object { $_.Name -eq $autoLoginUser } | Select-Object -First 1 }
    if (-not $pick) { $pick = $users | Sort-Object Timestamp -Descending | Select-Object -First 1 }
    $pick.Id
}

function Get-SteamAccountId {
    $au = (Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue).ActiveUser
    if ($au -and [int64]$au -ne 0) { return [int64]$au }
    $lu = Join-Path (Get-SteamPath) 'config\loginusers.vdf'
    if (-not (Test-Path -LiteralPath $lu)) { return $null }
    $auto = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).AutoLoginUser
    Select-SteamAccount (Get-Content -Raw -LiteralPath $lu) $auto { param($id) Test-Path -LiteralPath (Get-LocalConfigPath $id) }
}

function ConvertTo-VdfString([string]$s) { $s.Replace('\', '\\').Replace('"', '\"') }
function ConvertFrom-VdfString([string]$s) { [regex]::Replace($s, '\\(.)', '$1') }

# Locates a block by the suffix of its key path; returns open/close character offsets and direct string pairs.
function Find-VdfBlock([string]$text, [string[]]$suffix) {
    $tokens = [regex]::Matches($text, '"((?:\\.|[^"\\])*)"|\{|\}')
    $stack = New-Object System.Collections.Generic.List[string]
    $pending = $null
    for ($i = 0; $i -lt $tokens.Count; $i++) {
        $v = $tokens[$i].Value
        if ($v -eq '{') {
            $stack.Add($pending); $pending = $null
            if ($stack.Count -ge $suffix.Count) {
                $match = $true
                for ($k = 0; $k -lt $suffix.Count; $k++) { if ($stack[$stack.Count - $suffix.Count + $k] -ne $suffix[$k]) { $match = $false; break } }
                if ($match) {
                    $depth = 1; $pairs = @{}; $key = $null
                    for ($j = $i + 1; $j -lt $tokens.Count; $j++) {
                        $w = $tokens[$j].Value
                        if ($w -eq '{') { $depth++; $key = $null }
                        elseif ($w -eq '}') { $depth--; $key = $null; if ($depth -eq 0) { break } }
                        elseif ($depth -eq 1) {
                            if ($null -eq $key) { $key = $tokens[$j].Groups[1].Value }
                            else { $pairs[$key.ToLowerInvariant()] = $tokens[$j]; $key = $null }
                        }
                    }
                    return [pscustomobject]@{ Open = $tokens[$i].Index; Close = $tokens[$j].Index; Pairs = $pairs }
                }
            }
        }
        elseif ($v -eq '}') { if ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1) }; $pending = $null }
        elseif ($null -eq $pending) { $pending = $tokens[$i].Groups[1].Value }
        else { $pending = $null }
    }
    $null
}

function Get-LineIndent([string]$text, [int]$pos) {
    $start = $text.LastIndexOf("`n", [Math]::Max(0, $pos - 1)) + 1
    [regex]::Match($text.Substring($start), '^[\t ]*').Value
}

# Returns the current launch options of the game for an account ('' if none).
function Get-KHLaunchOption([int64]$accountId) {
    $path = Get-LocalConfigPath $accountId
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $text = [IO.File]::ReadAllText($path)
    $block = Find-VdfBlock $text @('Steam', 'apps', $script:AppId)
    if ($block -and $block.Pairs.ContainsKey('launchoptions')) { return (ConvertFrom-VdfString $block.Pairs['launchoptions'].Groups[1].Value) }
    ''
}

# Writes the launch options (Steam must not be running, it rewrites the file on exit). Returns the previous value.
function Set-KHLaunchOptionText([int64]$accountId, [string]$value) {
    $path = Get-LocalConfigPath $accountId
    if (-not (Test-Path -LiteralPath $path)) { throw "Steam config not found: $path" }
    $text = [IO.File]::ReadAllText($path)
    $escaped = '"' + (ConvertTo-VdfString $value) + '"'
    $previous = ''
    $block = Find-VdfBlock $text @('Steam', 'apps', $script:AppId)
    if ($block -and $block.Pairs.ContainsKey('launchoptions')) {
        $tok = $block.Pairs['launchoptions']
        $previous = ConvertFrom-VdfString $tok.Groups[1].Value
        $new = $text.Substring(0, $tok.Index) + $escaped + $text.Substring($tok.Index + $tok.Length)
    }
    elseif ($block) {
        $indent = (Get-LineIndent $text $block.Open) + "`t"
        $new = $text.Insert($block.Open + 1, "`n$indent`"LaunchOptions`"`t`t$escaped")
    }
    else {
        $apps = Find-VdfBlock $text @('Steam', 'apps')
        if (-not $apps) { throw 'Could not find the apps section in localconfig.vdf' }
        $indent = (Get-LineIndent $text $apps.Open) + "`t"
        $new = $text.Insert($apps.Open + 1, "`n$indent`"$($script:AppId)`"`n$indent{`n$indent`t`"LaunchOptions`"`t`t$escaped`n$indent}")
    }
    New-Item -ItemType Directory -Force -Path $script:StateDir | Out-Null
    Copy-Item -LiteralPath $path -Destination (Join-Path $script:StateDir ('localconfig-' + $accountId + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.vdf.bak'))
    [IO.File]::WriteAllText($path, $new, (New-Object Text.UTF8Encoding($false)))
    if ((Get-KHLaunchOption $accountId) -ne $value) { throw 'Verification of the written launch option failed' }
    $previous
}

# ---- launch option composition ------------------------------------------------------------------
function Test-OwnLaunchOption([string]$option) {
    "$option".IndexOf('"' + $script:Wrapper + '" %command%', [StringComparison]::OrdinalIgnoreCase) -ge 0
}

# Launch option that starts the game through this copy of the hotfix, keeping everything else the user had.
function Get-WrapperLaunchOption([string]$current) {
    $current = "$current".Trim()
    $ours = '"' + $script:Wrapper + '" %command%'
    if (Test-OwnLaunchOption $current) { return $current }
    if ($current -match $script:AnyWrapperPattern) { return [regex]::Replace($current, $script:AnyWrapperPattern, $ours.Replace('$', '$$')) }
    if ($current -match '%command%') { return $current.Replace('%command%', $ours) }
    if ($current) { return "$ours $current" }
    $ours
}

# Removes this copy of the hotfix from a launch option; other arguments stay.
function Remove-WrapperLaunchOption([string]$current) {
    $r = ([regex]::Replace("$current", [regex]::Escape('"' + $script:Wrapper + '"') + '\s+%command%', '%command%')).Trim()
    if ($r -eq '%command%') { return '' }
    if ($r.StartsWith('%command% ')) { return $r.Substring(10).Trim() }
    $r
}

# ---- Steam process control ----------------------------------------------------------------------
function Test-SteamRunning { [bool](Get-Process -Name steam -ErrorAction SilentlyContinue) }
function Test-GameRunning { [bool](Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'KINGDOM HEARTS*' }) }

function Stop-SteamGracefully {
    Write-Info 'Closing Steam...'
    Start-Process -FilePath (Join-Path (Get-SteamPath) 'steam.exe') -ArgumentList '-shutdown'
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Seconds 1
        if (-not (Get-Process -Name steam, steamwebhelper -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2; return $true }
    }
    $false
}

function Start-Steam { Start-Process -FilePath (Join-Path (Get-SteamPath) 'steam.exe') }

# Runs $action while Steam is closed. Asks first unless -Force. Returns $false if the user declined.
function Invoke-WithSteamClosed([scriptblock]$action, [switch]$Force) {
    $wasRunning = Test-SteamRunning
    if ($wasRunning) {
        if (Test-GameRunning) { throw 'KINGDOM HEARTS is running - quit the game first so Steam can be restarted.' }
        if (-not $Force) {
            $answer = Read-Host '   Steam must be closed for a moment to change the launch option. Close and restart Steam now? [Y/n]'
            if ($answer -and $answer -notmatch $script:YesPattern) { return $false }
        }
        if (-not (Stop-SteamGracefully)) { throw 'Steam did not close within 90 seconds' }
    }
    try { $null = & $action }
    finally { if ($wasRunning) { Start-Steam } }
    $true
}
