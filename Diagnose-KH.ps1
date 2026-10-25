<#
.SYNOPSIS
  Writes a diagnostic report for KINGDOM HEARTS -HD 1.5+2.5 ReMIX- crashes and save problems to Reports\diagnose-*.txt.
#>
[CmdletBinding()]
param([int]$Days = 30)
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'KHHotfix.Common.ps1')

$sb = New-Object System.Text.StringBuilder
$advice = New-Object System.Collections.Generic.List[string]
function Add([string]$line) { [void]$sb.AppendLine($line); Write-Host $line }
function Section([string]$t) { Add ''; Add "=== $t ===" }

Add "KH 1.5+2.5 Hotfix diagnostics - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$os = Get-CimInstance Win32_OperatingSystem
Add "Windows: $($os.Caption) $($os.Version)"

Section 'Game install'
$kh = Find-KHInstall
if (-not $kh) { Add 'NOT FOUND in any Steam library'; $advice.Add('Install the game through Steam.') }
else {
    Add "Path: $($kh.Path)"
    Add "Steam build: $($kh.BuildId)"
    foreach ($f in Get-ChildItem -LiteralPath $kh.Path -File) {
        if ($f.Extension -notin '.exe', '.dll', '.asi', '.ini', '.lua', '.dmp') { continue }
        $sig = (Get-AuthenticodeSignature -LiteralPath $f.FullName).Status
        $mark = if ($f.Extension -in '.exe', '.dll' -and $sig -ne 'Valid') { '  <- not signed by Square Enix/Valve (mod or modified file)' } elseif ($f.Extension -in '.asi', '.ini', '.lua', '.dmp') { '  <- extra file' } else { '' }
        Add ('  {0,-48} {1,12:N0}  {2:yyyy-MM-dd}  {3}{4}' -f $f.Name, $f.Length, $f.LastWriteTime, $sig, $mark)
    }
    $hooks = @(Get-ChildItem -LiteralPath $kh.Path -File | Where-Object { $_.Name -match '^(dbghelp|version|dinput8|d3d12|d3d11|dxgi|winmm|xinput1_\d)\.dll$|\.asi$|reshade|LuaBackend' })
    if ($hooks) { $advice.Add("Mod/hook files in the game folder ($($hooks.Name -join ', ')): remove them when testing crashes - mods made for older builds crash the current Steam build.") }
    $acct = Get-SteamAccountId
    if ($acct) {
        $sid = $acct + $script:SteamId64Base
        Add "Steam user: $sid$(if ($kh.LastOwner -and $kh.LastOwner -ne "$sid") { "  (game licence from $($kh.LastOwner) - Family Sharing)" })"
        $lo = Get-KHLaunchOption $acct
        Add "Launch options: '$lo'"
        $other = [regex]::Match($lo, '"([^"]*\\KHHotfix\.exe)"\s+%command%')
        if (Test-OwnLaunchOption $lo) { }
        elseif ($other.Success -and -not (Test-Path -LiteralPath $other.Groups[1].Value)) { $advice.Add("Steam starts the game through a hotfix folder that no longer exists ($($other.Groups[1].Value)), so the game cannot start. Run Install.cmd again, or clear the launch option in Steam.") }
        elseif ($other.Success) { Add "  (Steam uses another copy of the hotfix: $($other.Groups[1].Value))" }
        else { $advice.Add('The hotfix is not in the Steam launch options - run Install.cmd.') }
    }
}

Section 'Saves'
Add ((Invoke-Cli @('--paths')).Output)
$chk = Invoke-Cli @('--check'); Add $chk.Output
if ($chk.Code -eq 5) { $advice.Add('A save file failed validation - run Restore.cmd and pick the newest version that is not marked damaged.') }
$docs = [Environment]::GetFolderPath('MyDocuments')
foreach ($ev in 'OneDrive', 'OneDriveConsumer', 'OneDriveCommercial') {
    $od = [Environment]::GetEnvironmentVariable($ev)
    if ($od -and $docs.StartsWith($od, [StringComparison]::OrdinalIgnoreCase)) { Add "Documents is synced by OneDrive ($od)"; $advice.Add('Documents is in OneDrive: pause sync while playing or exclude My Games\KINGDOM HEARTS HD 1.5+2.5 ReMIX.') }
}
try {
    $mp = Get-MpPreference
    Add "Controlled Folder Access: $(switch ($mp.EnableControlledFolderAccess) { 0 {'off'} 1 {'ON'} 2 {'audit'} default {$_} })"
    if ($mp.EnableControlledFolderAccess -eq 1 -and -not (Test-Path (Join-Path $script:StateDir 'cfa-allowed.txt'))) { $advice.Add('Controlled Folder Access is ON and blocks saving: run Install.cmd again and answer Y when it offers to allow the game.') }
} catch { Add 'Controlled Folder Access: unknown' }
$sac = Get-SmartAppControlState
Add "Smart App Control: $sac"
if ($sac -eq 'on') { $advice.Add('Smart App Control is on: it blocks unsigned programs such as KHHotfix.exe. If the game does not start, uninstall the hotfix.') }
$drive = Get-PSDrive -Name $docs.Substring(0, 1) -ErrorAction SilentlyContinue
if ($drive) { Add ('Free space on {0}: {1:N1} GB' -f $drive.Name, ($drive.Free / 1GB)) }

Section 'Code fixes (applied in memory while the game runs)'
if ($kh) {
    $fx = Invoke-Cli @('--fixes-scan', $kh.Path)
    Add $fx.Output
    if ($fx.Code -ne 0) { $advice.Add('Some code fixes do not match this game version (the game was probably updated) - check for a newer KH-Hotfix.') }
}

Section 'Display and GPU'
Add ((Invoke-Cli @('--display-info')).Output)
foreach ($g in Get-CimInstance Win32_VideoController) { Add "GPU: $($g.Name)  driver $($g.DriverVersion) ($('{0:yyyy-MM-dd}' -f $g.DriverDate))" }
if ((Get-CimInstance Win32_VideoController).Name -match 'Radeon RX 7\d{3}|Radeon 7[4-8]0M|Z1') { $advice.Add('RDNA3 Radeon GPUs are the worst affected by this port - keep the AMD driver current and stay at 60 FPS.') }
$nv = Invoke-Cli @('--nv-status')
if ($nv.Code -eq 0) { Add $nv.Output; if ($nv.Output -notmatch 'APPLIED') { $advice.Add('NVIDIA 60 FPS cap not applied - run Install.cmd.') } }

Section 'Audio'
$root = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
foreach ($d in Get-ChildItem $root -ErrorAction SilentlyContinue) {
    if ((Get-ItemProperty $d.PSPath).DeviceState -ne 1) { continue }
    $p = Get-ItemProperty (Join-Path $d.PSPath 'Properties') -ErrorAction SilentlyContinue
    $fmt = $p.'{f19f064d-082c-4e27-bc73-6882a1bb8e4c},0'
    $name = "$($p.'{a45c254e-df1c-4efd-8020-67d146a850e0},2') ($($p.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6'))"
    if ($fmt) {
        $ch = [BitConverter]::ToUInt16($fmt, 10); $hz = [BitConverter]::ToUInt32($fmt, 12)
        Add "  $name : $ch ch, $hz Hz"
        if ($ch -gt 2) { $advice.Add("Audio device '$name' is set to $ch channels - the game crackles/cuts out; set it to Stereo (2 ch) in Sound settings.") }
    }
    if ($name -match 'Hands-Free') { $advice.Add('A Bluetooth headset is present: if its microphone is used (e.g. Discord), Windows switches it to Hands-Free mode, which can drop or crash game audio. Use a wired/USB output or keep the headset mic unused while playing.') }
}

Section 'Overlays and hooks currently running'
$ov = Get-Process | Where-Object { $_.ProcessName -match '^(Discord|DiscordPTB|DiscordCanary|RTSS|MSIAfterburner|Overwolf|NVIDIA Overlay|obs64|Medal|ReShade|LosslessScaling|SteelSeriesGG|ArmouryCrate)' } | Select-Object -ExpandProperty ProcessName -Unique
Add $(if ($ov) { $ov -join ', ' } else { 'none detected' })
if ($ov) { $advice.Add("Overlays inject into the game ($($ov -join ', ')). Disable their in-game overlay for KH if crashes continue.") }

Section "Crash history (last $Days days)"
$since = (Get-Date).AddDays(-$Days)
$ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1001, 1002, 1026; StartTime = $since } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'KINGDOM HEARTS' })
Add "Windows crash/hang events for KH: $($ev.Count)"
foreach ($e in $ev | Select-Object -First 15) { Add ("  [{0:yyyy-MM-dd HH:mm}] {1} {2}: {3}" -f $e.TimeCreated, $e.ProviderName, $e.Id, (($e.Message -replace '\s+', ' ').Substring(0, [Math]::Min(220, ($e.Message -replace '\s+', ' ').Length)))) }
$rep = @(Get-ChildItem (Join-Path $script:HotfixRoot 'Reports') -Directory -ErrorAction SilentlyContinue)
Add "Hotfix crash/hang reports: $($rep.Count)$(if ($rep) { ' (newest: ' + ($rep | Sort-Object Name | Select-Object -Last 1).FullName + ')' })"
if ($kh) { $d = @(Get-ChildItem -LiteralPath $kh.Path -Filter *.dmp -ErrorAction SilentlyContinue); if ($d) { Add "Game crash dumps in game folder: $($d.Name -join ', ')" } }
$whea = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = $since } -ErrorAction SilentlyContinue)
$tdr = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'nvlddmkm', 'amdkmdag', 'Display'; StartTime = $since } -ErrorAction SilentlyContinue)
Add "Hardware (WHEA) errors: $($whea.Count)   GPU driver resets/errors: $($tdr.Count)"
if ($whea.Count -gt 0) { $advice.Add('WHEA hardware errors are logged: the PC itself is unstable (check CPU/RAM overclocks, XMP and BIOS updates) - this crashes games regardless of the hotfix.') }
if ($tdr.Count -gt 0) { $advice.Add('GPU driver resets are logged: update or clean-install the GPU driver and remove GPU overclocks.') }
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
Add "CPU: $cpu"
if ($cpu -match 'i[579]-1[34]\d{3}') {
    $rev = [Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'Update Revision', $null)
    if ($rev -is [byte[]] -and $rev.Length -ge 4) { $rv = [BitConverter]::ToUInt32($rev, $rev.Length - 4); Add ('Microcode revision: 0x{0:X}' -f $rv); if ($rv -lt 0x12B) { $advice.Add('Intel 13th/14th gen CPU with old microcode: install the latest BIOS (microcode 0x12B or newer) - affected CPUs crash games randomly.') } }
}

Section 'Recommendations'
if ($advice.Count -eq 0) { Add 'No problems found.' } else { $i = 1; foreach ($a in $advice) { Add "  $i. $a"; $i++ } }
Add '  In-game: Screen Mode = Borderless Windowed, Frame Rate = 60, Refresh Rate = ON.'

$outDir = Join-Path $script:HotfixRoot 'Reports'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir ('diagnose-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
[IO.File]::WriteAllText($out, $sb.ToString())
Write-Host ''
Write-Host "Report saved to $out" -ForegroundColor Green
