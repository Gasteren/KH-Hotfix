<#
.SYNOPSIS
  Restores an earlier version of a KINGDOM HEARTS save from the hotfix backups.
.EXAMPLE
  .\Restore-KHSave.ps1                                  # interactive
  .\Restore-KHSave.ps1 -Save KHIIFM_WW.png -LatestGood -Yes
#>
[CmdletBinding()]
param([string]$Save, [int]$Version = 0, [switch]$LatestGood, [switch]$Yes)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'KHHotfix.Common.ps1')

$names = @{
    'KHFM_WW.png' = 'Kingdom Hearts Final Mix'; 'KHReCoM_WW.png' = 'Re:Chain of Memories'; 'KHIIFM_WW.png' = 'Kingdom Hearts II Final Mix'
    'KHBbSFM_WW.png' = 'Birth by Sleep Final Mix'; 'KH1.5+2.5_system_WW.png' = 'Collection system data'; 'KHFM_Theater_WW.png' = 'Theater'
    'config1525.dat' = 'Settings'
}
$backupRoot = Join-Path $script:HotfixRoot 'Backups'
$savesRoot = (& $script:Cli --paths | Select-String '^Saves folder\s*:\s*(.+)$').Matches[0].Groups[1].Value.Trim()

$sets = @(Get-ChildItem -LiteralPath $backupRoot -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notlike '*\_quarantine*' -and (Get-ChildItem -LiteralPath $_.FullName -Filter *.gz -File -ErrorAction SilentlyContinue) } |
    ForEach-Object {
        $rel = $_.FullName.Substring($backupRoot.Length + 1)
        $versions = @(Get-ChildItem -LiteralPath $_.FullName -Filter *.gz -File | Sort-Object Name -Descending | ForEach-Object {
            $p = $_.BaseName -split '_'
            [pscustomobject]@{ File = $_.FullName; Time = [datetime]::ParseExact($p[0], 'yyyyMMdd-HHmmss-fff', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]'AssumeUniversal, AdjustToUniversal').ToLocalTime(); Size = [int64]$p[2]; Status = $p[3] }
        })
        $leaf = Split-Path $rel -Leaf
        [pscustomobject]@{ Rel = $rel; Leaf = $leaf; Label = $(if ($names.ContainsKey($leaf)) { $names[$leaf] } else { $leaf }); Target = Join-Path $savesRoot $rel; Versions = $versions }
    })
if ($sets.Count -eq 0) { Write-Host 'No backups yet. They are created automatically every time you play through the hotfix.'; exit 0 }

if ($Save) { $set = $sets | Where-Object { $_.Rel -like "*$Save" -or $_.Leaf -eq $Save } | Select-Object -First 1; if (-not $set) { throw "No backups for '$Save'" } }
else {
    Write-Host 'Save files with backups:' -ForegroundColor Cyan
    for ($i = 0; $i -lt $sets.Count; $i++) {
        Write-Host ('  [{0}] {1,-30} {2,3} versions, newest {3:g}   ({4})' -f ($i + 1), $sets[$i].Label, $sets[$i].Versions.Count, $sets[$i].Versions[0].Time, $sets[$i].Rel)
    }
    $n = [int](Read-Host 'Which save? (number)')
    if ($n -lt 1 -or $n -gt $sets.Count) { throw 'Invalid choice' }
    $set = $sets[$n - 1]
}

$current = if (Test-Path -LiteralPath $set.Target) { Get-Item -LiteralPath $set.Target } else { $null }
if ($LatestGood) { $pick = $set.Versions | Where-Object { $_.Status -eq 'Ok' -or $_.Status -eq 'Raw' } | Select-Object -First 1 }
elseif ($Version -gt 0) { $pick = $set.Versions[$Version - 1] }
else {
    Write-Host ''
    Write-Host "$($set.Label) - current file: $(if ($current) { '{0:g}, {1:N0} bytes' -f $current.LastWriteTime, $current.Length } else { 'missing' })" -ForegroundColor Cyan
    $show = [Math]::Min(25, $set.Versions.Count)
    for ($i = 0; $i -lt $show; $i++) {
        $v = $set.Versions[$i]
        $flag = if ($v.Status -in 'Ok', 'Raw') { '' } else { "  <- damaged ($($v.Status))" }
        Write-Host ('  [{0,2}] {1:ddd dd MMM yyyy HH:mm:ss}  {2,12:N0} bytes{3}' -f ($i + 1), $v.Time, $v.Size, $flag)
    }
    $n = [int](Read-Host 'Restore which version? (number)')
    if ($n -lt 1 -or $n -gt $show) { throw 'Invalid choice' }
    $pick = $set.Versions[$n - 1]
}
if (-not $pick) { throw 'No suitable backup version found' }

Write-Host ''
Write-Host "Restore $($set.Label) from $($pick.Time.ToString('g')) -> $($set.Target)"
Write-Host 'The current file is kept in Backups\_quarantine first. Steam Cloud will upload the restored file next time you play.'
if (-not $Yes) { $a = Read-Host 'Continue? [y/N]'; if ($a -notmatch '^(y|yes|j|ja)$') { Write-Host 'Cancelled.'; exit 1 } }
$r = Invoke-Cli @('--restore-backup', $pick.File, $set.Target)
if ($r.Code -ne 0) { throw $r.Output }
Write-Host $r.Output -ForegroundColor Green
