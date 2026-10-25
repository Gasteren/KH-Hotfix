<#
.SYNOPSIS
  Installs or updates the KH 1.5+2.5 Hotfix for KINGDOM HEARTS -HD 1.5+2.5 ReMIX- (Steam). Safe to run again at any time.
.DESCRIPTION
  Copies the hotfix to %LOCALAPPDATA%\KH-Hotfix (unless it runs from a git clone or with -InPlace), builds it from the
  included source code and configures Steam, Windows and the NVIDIA driver for the KH executables only.
  No administrator rights are needed. Uninstall.cmd reverts every change.
.PARAMETER Admin
  Also run the steps that need administrator rights (UAC prompt): allow the KH executables through Controlled Folder
  Access when it is enabled, and let Windows keep crash dumps of them. Offered automatically when Controlled Folder
  Access is on.
.PARAMETER RestartSteam
  Close and restart Steam without asking (needed once to write the launch option).
.PARAMETER SkipSteam
  Do not change the Steam launch option; print it instead.
.PARAMETER NoNvidia
  Do not change NVIDIA driver settings.
.PARAMETER Fps
  Driver frame-rate cap for the KH executables (default 60, 0 = no cap).
.PARAMETER InPlace
  Run the hotfix from this folder instead of copying it to the install folder.
.PARAMETER InstallDir
  Install folder (default %LOCALAPPDATA%\KH-Hotfix).
.PARAMETER NoShortcuts
  Do not create the Start menu folder "KH Hotfix".
.PARAMETER NoClipboard
  Never copy the launch option to the clipboard.
#>
[CmdletBinding()]
param([switch]$Admin, [switch]$RestartSteam, [switch]$SkipSteam, [switch]$NoNvidia, [int]$Fps = 60, [switch]$InPlace,
      [string]$InstallDir, [switch]$NoShortcuts, [switch]$NoClipboard, [string]$ShortcutRoot, [switch]$AdminStepsOnly, [string]$GameDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'KHHotfix.Common.ps1')

function Get-CfaState {
    try { return [int](Get-MpPreference -ErrorAction Stop).EnableControlledFolderAccess } catch { return -1 }
}

function Invoke-AdminSteps([string]$gameDir) {
    Write-Step 'Administrator steps'
    New-Item -ItemType Directory -Force -Path $script:StateDir | Out-Null
    if ((Get-CfaState) -in 1, 2) {
        $apps = @($script:GameExes | ForEach-Object { Join-Path $gameDir $_ }) + $script:Wrapper + $script:Cli
        foreach ($a in $apps) { Add-MpPreference -ControlledFolderAccessAllowedApplications $a }
        Set-Content -LiteralPath (Join-Path $script:StateDir 'cfa-allowed.txt') -Value $apps -Encoding UTF8
        Write-Ok 'Controlled Folder Access: the KH executables may now write their saves'
    }
    else { Write-Ok 'Controlled Folder Access is off - nothing to allow' }

    $dumpDir = Join-Path $script:HotfixRoot 'CrashDumps'
    New-Item -ItemType Directory -Force -Path $dumpDir | Out-Null
    $base = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
    $created = @()
    foreach ($exe in $script:GameExes) {
        $key = Join-Path $base $exe
        if (-not (Test-Path -LiteralPath $key)) { New-Item -Path $key -Force | Out-Null; $created += $exe }
        New-ItemProperty -LiteralPath $key -Name DumpFolder -PropertyType ExpandString -Value $dumpDir -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name DumpType -PropertyType DWord -Value 1 -Force | Out-Null
        New-ItemProperty -LiteralPath $key -Name DumpCount -PropertyType DWord -Value 5 -Force | Out-Null
    }
    $previous = @()
    $ldFile = Join-Path $script:StateDir 'localdumps.txt'
    if (Test-Path -LiteralPath $ldFile) { $previous = @(Get-Content -LiteralPath $ldFile) }
    Set-Content -LiteralPath $ldFile -Value (@($previous) + $created | Where-Object { $_ } | Select-Object -Unique) -Encoding UTF8
    Write-Ok "Windows keeps crash dumps of the KH executables in $dumpDir"

    if (Test-Path -LiteralPath (Join-Path $script:StateDir 'nvidia-needs-admin.flag')) {
        $r = Invoke-Cli @('--nv-apply', '--fps', "$Fps")
        if ($r.Code -eq 0) { Write-Ok 'NVIDIA driver profile applied'; Remove-Item -LiteralPath (Join-Path $script:StateDir 'nvidia-needs-admin.flag') }
        else { Write-Warn2 $r.Output }
    }
}

if ($AdminStepsOnly) {
    try { Invoke-AdminSteps $GameDir } catch { Write-Warn2 $_.Exception.Message; Start-Sleep -Seconds 8; exit 1 }
    Start-Sleep -Seconds 2
    exit 0
}

Write-Host 'KH 1.5+2.5 Hotfix installer' -ForegroundColor White
$null = Unblock-HotfixFiles $script:HotfixRoot

Write-Step 'Locating the game'
$kh = Find-KHInstall
if (-not $kh) { throw "KINGDOM HEARTS -HD 1.5+2.5 ReMIX- (Steam app $($script:AppId)) is not installed in any Steam library on this PC. Nothing was changed." }
Write-Ok "$($kh.Path) (Steam build $($kh.BuildId))"
$sig = Get-AuthenticodeSignature -LiteralPath (Join-Path $kh.Path 'KINGDOM HEARTS FINAL MIX.exe')
if ($sig.Status -ne 'Valid') { Write-Warn2 'The game executables are modified or unsigned - verify the game files in Steam if the hotfix misbehaves.' }

# A downloaded copy is moved to a permanent folder, because Steam starts the game through it.
$here = $script:HotfixRoot.TrimEnd('\')
if (-not $InstallDir) { $InstallDir = $script:ManagedDir }
$target = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
$isClone = Test-Path -LiteralPath (Join-Path $here '.git')
if (-not $InPlace -and -not $isClone -and -not $here.Equals($target, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Step "Installing to $target"
    if ((Get-RunningHotfix $target).Count -gt 0) { throw 'KINGDOM HEARTS is running through the hotfix. Quit the game, then run Install.cmd again.' }
    if (Copy-HotfixFiles $here $target) { Write-Ok 'Copied' } else { Write-Ok 'Updated (your KHHotfix.ini settings were kept)' }
    $merged = @(Merge-LocalIni $here $target)
    if ($merged.Count -gt 0) { Write-Ok ('Settings from KHHotfix.local.ini added: ' + ($merged -join ', ')) }
    Write-Info 'The downloaded folder is not needed any more after this.'
    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $target 'Install-KHHotfix.ps1'), '-InPlace', '-InstallDir', $target)
    foreach ($k in $PSBoundParameters.Keys) {
        if ($k -in 'InPlace', 'InstallDir') { continue }
        $v = $PSBoundParameters[$k]
        if ($v -is [Management.Automation.SwitchParameter]) { if ($v.IsPresent) { $pass += "-$k" } }
        else { $pass += "-$k"; $pass += [string]$v }
    }
    & powershell.exe @pass
    exit $LASTEXITCODE
}
$managed = $here.Equals($target, [StringComparison]::OrdinalIgnoreCase)
if ((Get-RunningHotfix $here).Count -gt 0) { throw 'KINGDOM HEARTS is running through the hotfix. Quit the game, then run Install.cmd again.' }

Write-Step 'Building the hotfix from its source code'
Invoke-HotfixBuild $here
try { $r = Invoke-Cli @('--selftest') }
catch {
    $why = if ((Get-SmartAppControlState) -eq 'on') { 'Smart App Control is on and blocks programs that are not signed by a known publisher, so the hotfix cannot be used on this PC.' } else { 'An antivirus program probably blocked it.' }
    throw "$($_.Exception.Message) $why Nothing on your PC was changed."
}
if ($r.Code -ne 0) { Write-Host $r.Output; throw 'The hotfix self-test failed. Nothing on your PC was changed.' }
Write-Ok 'KHHotfix.exe and KHHotfixCli.exe built; self-test passed'

# Only checks the webhooks (nothing is posted). Exit code 3: Discord reporting is not set up.
$r = Invoke-Cli @('--discord-test')
if ($r.Code -ne 3) {
    Write-Step 'Discord reporting'
    $r.Output -split "`r?`n" | Where-Object { $_ } | ForEach-Object { Write-Info $_ }
    if ($r.Code -eq 0) { Write-Ok 'Bug reports and crash reports will be posted to Discord' }
    else { Write-Warn2 'Discord reporting does not work yet (see above). The game and the rest of the hotfix are not affected.' }
}

$state = Get-HotfixState
if (-not $state.InstalledAt) { $state.InstalledAt = (Get-Date).ToString('s') }
$state.Managed = $managed
Save-HotfixState $state

Write-Step 'Windows graphics preference (High performance GPU)'
$gpuKey = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
if (-not (Test-Path $gpuKey)) { New-Item -Path $gpuKey -Force | Out-Null }
$recorded = @{}
foreach ($p in @($state.GpuPrefs)) { if ($p) { $recorded[$p.Name] = $p } }
foreach ($exe in $script:GameExes) {
    $full = Join-Path $kh.Path $exe
    if (-not $recorded.ContainsKey($full)) {
        $prev = (Get-ItemProperty -Path $gpuKey -Name $full -ErrorAction SilentlyContinue).$full
        $recorded[$full] = [pscustomobject]@{ Name = $full; Previous = $prev }
    }
    New-ItemProperty -Path $gpuKey -Name $full -Value 'GpuPreference=2;' -PropertyType String -Force | Out-Null
}
$state.GpuPrefs = @($recorded.Values)
Save-HotfixState $state
Write-Ok "$($script:GameExes.Count) executables set to the high-performance GPU"

Write-Step "NVIDIA driver profile (frame cap $Fps FPS, V-Sync on, G-SYNC off - KH executables only)"
if ($NoNvidia) { Write-Info 'Skipped (-NoNvidia)' }
else {
    try {
        $r = Invoke-Cli @('--nv-apply', '--fps', "$Fps")
        switch ($r.Code) {
            0 { $r.Output -split "`r?`n" | ForEach-Object { Write-Info $_ }; Write-Ok 'Applied' }
            3 { New-Item -ItemType File -Force -Path (Join-Path $script:StateDir 'nvidia-needs-admin.flag') | Out-Null; Write-Warn2 'Needs administrator rights - run Install.cmd -Admin' }
            4 { Write-Info 'No NVIDIA graphics card - skipped. Set Frame Rate = 60 in the game''s System Settings instead.' }
            default { Write-Warn2 $r.Output }
        }
    }
    catch { Write-Warn2 $_.Exception.Message }
}

Write-Step 'Backing up your current saves'
$r = Invoke-Cli @('--backup')
Write-Ok $r.Output

$cfa = Get-CfaState
if (-not $Admin -and $cfa -eq 1 -and -not (Test-Path -LiteralPath (Join-Path $script:StateDir 'cfa-allowed.txt'))) {
    Write-Step 'Windows ransomware protection'
    Write-Warn2 'Controlled Folder Access is on. It silently blocks the game from writing its saves ("Failed to save game data").'
    $a = Read-Host '   Allow the KINGDOM HEARTS programs to save now? Windows will ask for administrator permission. [Y/n]'
    if (-not $a -or $a -match $script:YesPattern) { $Admin = [switch]$true }
}
if ($Admin) {
    if (Test-IsAdmin) { Invoke-AdminSteps $kh.Path }
    else {
        Write-Step 'Administrator steps (Windows asks for permission)'
        $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -AdminStepsOnly -Fps $Fps -GameDir `"$($kh.Path)`""
        try {
            $p = Start-Process -FilePath powershell.exe -ArgumentList $argList -Verb RunAs -Wait -PassThru
            if ($p.ExitCode -eq 0) { Write-Ok 'Administrator steps completed' } else { Write-Warn2 'Administrator steps failed' }
        }
        catch { Write-Warn2 'Administrator permission was not given - skipped' }
    }
}

Write-Step 'Steam launch option'
$account = Get-SteamAccountId
$current = if ($account) { Get-KHLaunchOption $account } else { '' }
$wanted = Get-WrapperLaunchOption $current
$manual = $SkipSteam -or -not $account
if (-not $account) { Write-Warn2 'No Steam account found on this PC.' }
if ($current -eq $wanted) { Write-Ok "Already set: $wanted"; $manual = $false }
elseif (-not $manual) {
    if ($current -match $script:AnyWrapperPattern) { Write-Info "Replacing the hotfix from another folder: $current" }
    elseif ($current -match '%command%') { Write-Info "Keeping your other launch tool: $current" }
    try {
        $done = Invoke-WithSteamClosed -Force:$RestartSteam {
            $null = Set-KHLaunchOptionText $account $wanted
            $state.LaunchOption = [pscustomobject]@{ AccountId = $account; Value = $wanted }
            Save-HotfixState $state
        }
        if ($done) { Write-Ok "Set: $wanted" } else { $manual = $true }
    }
    catch { Write-Warn2 "Could not change it automatically: $($_.Exception.Message)"; $manual = $true }
}
if ($manual) {
    Write-Info 'Set it yourself: Steam > Library > right-click KINGDOM HEARTS -HD 1.5+2.5 ReMIX- > Properties > General > Launch options:'
    Write-Host "      $wanted" -ForegroundColor White
    if (-not $NoClipboard) { try { Set-Clipboard -Value $wanted; Write-Info '(copied to the clipboard - just paste it there)' } catch { } }
}

if (-not $NoShortcuts) {
    Write-Step 'Start menu'
    if (-not $ShortcutRoot) { $ShortcutRoot = [Environment]::GetFolderPath('Programs') }
    try {
        $state.Shortcuts = New-HotfixShortcuts $here $kh.Path $ShortcutRoot
        Save-HotfixState $state
        Write-Ok "Start menu > $($script:ShortcutFolderName): report a bug, restore a save, diagnostics, uninstall, folder"
    }
    catch { Write-Warn2 "Could not create Start menu shortcuts: $($_.Exception.Message)" }
}

Write-Step 'Done'
Write-Host "   Installed in $here"
Write-Host @"

   Start KINGDOM HEARTS from Steam as usual - the hotfix runs automatically.

   Recommended in-game settings (KH launcher > System Settings, used by all games):
     Screen Mode  : Borderless Windowed   (exclusive fullscreen crashes on Alt+Tab)
     Frame Rate   : 60                    (the engine is crash-prone above 60 FPS)
     Refresh Rate : ON

   While playing: Ctrl+Alt+B reports a bug (with a screenshot of the game).
                  Ctrl+Alt+K backs up your saves and force-closes a frozen game.
   Report a bug, restore a save, diagnostics, uninstall: Start menu > KH Hotfix (or the .cmd files in the folder above).
"@
