<#
.SYNOPSIS
  Removes everything the KH 1.5+2.5 Hotfix changed: Steam launch option, NVIDIA profile settings, GPU preferences,
  Start menu shortcuts, Controlled Folder Access entries and crash-dump settings.
  Your real save files are never touched. The hotfix's own save backups are kept unless you choose to delete them.
.PARAMETER RemoveData
  Also delete the hotfix's backups, logs and reports (and the whole hotfix folder when it was installed by Install.cmd).
.PARAMETER KeepData
  Never ask about deleting the hotfix folder.
.PARAMETER RestartSteam
  Close and restart Steam without asking.
#>
[CmdletBinding()]
param([switch]$RestartSteam, [switch]$RemoveData, [switch]$KeepData, [switch]$AdminStepsOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'KHHotfix.Common.ps1')

function Invoke-AdminRevert {
    $cfa = Join-Path $script:StateDir 'cfa-allowed.txt'
    if (Test-Path -LiteralPath $cfa) {
        foreach ($a in Get-Content -LiteralPath $cfa) { if ($a) { Remove-MpPreference -ControlledFolderAccessAllowedApplications $a -ErrorAction SilentlyContinue } }
        Remove-Item -LiteralPath $cfa
        Write-Ok 'Controlled Folder Access entries removed'
    }
    $ld = Join-Path $script:StateDir 'localdumps.txt'
    if (Test-Path -LiteralPath $ld) {
        foreach ($exe in Get-Content -LiteralPath $ld) {
            if ($exe) { Remove-Item -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$exe" -Recurse -ErrorAction SilentlyContinue }
        }
        Remove-Item -LiteralPath $ld
        Write-Ok 'Crash dump settings removed'
    }
    if (Test-Path -LiteralPath (Join-Path $script:StateDir 'nvidia.txt')) { $r = Invoke-Cli @('--nv-revert'); Write-Info $r.Output }
}

if ($AdminStepsOnly) {
    try { Invoke-AdminRevert } catch { Write-Warn2 $_.Exception.Message; Start-Sleep -Seconds 8; exit 1 }
    Start-Sleep -Seconds 2
    exit 0
}

$forward = Get-InstalledCopyCommand 'Uninstall-KHHotfix.ps1' $PSBoundParameters
if ($forward) {
    Write-Host "Uninstalling the hotfix installed in $(Split-Path -Parent $forward[4])"
    & powershell.exe @forward
    exit $LASTEXITCODE
}

Write-Host "KH 1.5+2.5 Hotfix uninstaller - $script:HotfixRoot" -ForegroundColor White
if ((Get-RunningHotfix $script:HotfixRoot).Count -gt 0) { throw 'KINGDOM HEARTS is running through the hotfix. Quit the game first, then run the uninstaller again.' }
$state = Get-HotfixState
$haveBinaries = Test-Path -LiteralPath $script:Cli

Write-Step 'Display'
if ($haveBinaries) { $r = Invoke-Cli @('--restore-display'); Write-Ok $r.Output } else { Write-Ok 'Nothing to restore' }

Write-Step 'NVIDIA driver profile'
if ((Test-Path -LiteralPath (Join-Path $script:StateDir 'nvidia.txt')) -and $haveBinaries) {
    $r = Invoke-Cli @('--nv-revert')
    if ($r.Code -eq 0) { Write-Ok 'Reverted' } else { Write-Warn2 "$($r.Output) - will retry with administrator rights" }
}
else { Write-Ok 'Nothing to revert' }

Write-Step 'Windows graphics preference'
$gpuKey = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
foreach ($p in @($state.GpuPrefs)) {
    if (-not $p) { continue }
    if ($p.Previous) { New-ItemProperty -Path $gpuKey -Name $p.Name -Value $p.Previous -PropertyType String -Force | Out-Null }
    else { Remove-ItemProperty -Path $gpuKey -Name $p.Name -ErrorAction SilentlyContinue }
}
$state.GpuPrefs = @()
Save-HotfixState $state
Write-Ok 'Restored'

$needAdmin = @('cfa-allowed.txt', 'localdumps.txt', 'nvidia.txt') | Where-Object { Test-Path -LiteralPath (Join-Path $script:StateDir $_) }
if ($needAdmin) {
    Write-Step 'Administrator steps (Windows asks for permission)'
    if (Test-IsAdmin) { Invoke-AdminRevert }
    else {
        try {
            $p = Start-Process -FilePath powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -AdminStepsOnly" -Verb RunAs -Wait -PassThru
            if ($p.ExitCode -eq 0) { Write-Ok 'Done' } else { Write-Warn2 'Administrator steps failed' }
        }
        catch { Write-Warn2 'Administrator permission was not given - those settings were left in place' }
    }
}

Write-Step 'Steam launch option'
$launchClean = $true
$lo = $state.LaunchOption
$account = if ($lo -and $lo.AccountId) { [int64]$lo.AccountId } else { Get-SteamAccountId }
$current = if ($account) { Get-KHLaunchOption $account } else { '' }
if (-not (Test-OwnLaunchOption $current)) { Write-Ok 'Steam does not start the game through this copy of the hotfix' }
else {
    $restore = Remove-WrapperLaunchOption $current
    $done = $false
    try { $done = Invoke-WithSteamClosed -Force:$RestartSteam { $null = Set-KHLaunchOptionText $account $restore } }
    catch { Write-Warn2 $_.Exception.Message }
    if ($done) { Write-Ok "Launch option restored to: '$restore'"; $state.LaunchOption = $null; Save-HotfixState $state }
    else {
        $launchClean = $false
        Write-Warn2 'Not changed. In Steam > Library > right-click the game > Properties > General > Launch options, set it to:'
        Write-Host "      $restore" -ForegroundColor White
    }
}

Write-Step 'Start menu'
if ($state.Shortcuts) { Remove-HotfixShortcuts $state.Shortcuts; $state.Shortcuts = $null; Save-HotfixState $state; Write-Ok 'Removed' }
else { Write-Ok 'Nothing to remove' }

$deleteAll = $false
if ($state.Managed -and -not $KeepData) {
    if (-not $launchClean) { Write-Warn2 'The hotfix folder is kept because Steam still starts the game through it.' }
    elseif ($RemoveData) { $deleteAll = $true }
    else {
        Write-Host ''
        $a = Read-Host '   Also delete the hotfix folder with its save backups? Your real saves are not affected. [y/N]'
        $deleteAll = $a -match $script:YesPattern
    }
}
if ($deleteAll) {
    Remove-HotfixFolderLater $script:HotfixRoot
    Write-Step 'Uninstall finished'
    Write-Info "$script:HotfixRoot is deleted when this window closes."
    exit 0
}
if ($RemoveData) {
    foreach ($d in 'Backups', 'Logs', 'Reports', 'CrashDumps', 'State') { Remove-Item -LiteralPath (Join-Path $script:HotfixRoot $d) -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Ok 'Backups, logs and reports removed'
}
else { Write-Info "Your save backups are still in $(Join-Path $script:HotfixRoot 'Backups')" }
Write-Step 'Uninstall finished'
