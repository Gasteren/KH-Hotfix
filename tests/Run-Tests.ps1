<#
.SYNOPSIS
  End-to-end tests for KHHotfix.exe using a fake KINGDOM HEARTS game (tests\FakeKH.cs).
  Runs entirely in %TEMP%; never touches the real game, saves or display settings.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ('KHHotfix-it-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$hot = Join-Path $work 'hotfix'
$game = Join-Path $work 'game'
$saves = Join-Path $work 'saves'
New-Item -ItemType Directory -Force -Path $hot, $game, $saves | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$work\FakeKH.exe" (Join-Path $PSScriptRoot 'FakeKH.cs')
if ($LASTEXITCODE -ne 0) { throw 'FakeKH build failed' }
Copy-Item "$work\FakeKH.exe" (Join-Path $game 'KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe')
Copy-Item "$work\FakeKH.exe" (Join-Path $game 'KINGDOM HEARTS FINAL MIX.exe')
. (Join-Path $root 'KHHotfix.Common.ps1')
Invoke-HotfixBuild $root $hot   # fresh build from src\ (never uses or locks the binaries of a running hotfix)
$env:KHHOTFIX_INSTANCE = '.test'  # separate session lock, so the tests also run while a real game session is active
@"
TargetRefreshHz=0
ShowDialogs=0
QuitHangKillSeconds=5
HungWindowKillSeconds=6
ExitGraceSeconds=3
PanicHotkey=Ctrl+Alt+Shift+F11
"@ | Set-Content -Path (Join-Path $hot 'KHHotfix.ini') -Encoding ASCII

$env:KHHOTFIX_SAVES_ROOT = $saves
$script:failed = 0
function Expect([bool]$cond, [string]$what) {
    if ($cond) { Write-Host "  PASS  $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failed++ }
}
function Invoke-Session([string]$mode) {
    $env:FAKEKH_MODE = $mode
    $before = @(Get-ChildItem (Join-Path $hot 'Logs') -Filter 'session-*.log' -ErrorAction SilentlyContinue).Count
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath (Join-Path $hot 'KHHotfix.exe') -ArgumentList ('"' + (Join-Path $game 'KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe') + '" -testarg "two words"') -PassThru
    if (-not $p.WaitForExit(90000)) { $p.Kill(); throw "session '$mode' timed out" }
    $sw.Stop()
    Start-Sleep -Milliseconds 1100   # session log names have 1 s resolution
    $log = Get-ChildItem (Join-Path $hot 'Logs') -Filter 'session-*.log' | Sort-Object Name | Select-Object -Last 1
    [pscustomobject]@{ Exit = $p.ExitCode; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); Log = (Get-Content -Raw $log.FullName); NewLog = ((@(Get-ChildItem (Join-Path $hot 'Logs') -Filter 'session-*.log')).Count -gt $before) }
}
function Get-Reports { @(Get-ChildItem (Join-Path $hot 'Reports') -Directory -ErrorAction SilentlyContinue) }
function Get-SaveBackups { @(Get-ChildItem (Join-Path $hot 'Backups\Steam\76561190000000000\KHFM_WW.png') -Filter *.gz -ErrorAction SilentlyContinue) }

try {
    Write-Host 'Scenario: normal session'
    $r = Invoke-Session 'normal'
    Expect ($r.Exit -eq 0 -and $r.NewLog) 'wrapper exits 0 and writes a session log'
    Expect ($r.Log -match 'Tracking KINGDOM HEARTS FINAL MIX\.exe' -and $r.Log -match 'opened its window') 'game process and window tracked'
    Expect ($r.Log -match 'exited, code 0x00000000') 'clean exit recorded'
    Expect ((Get-SaveBackups | Where-Object Name -match '_Ok\.gz$').Count -ge 1) 'save backed up with status Ok'
    Expect ((Get-Content -Raw (Join-Path $work 'fake-cmdlines.txt')) -match 'ReMIX\.exe" -testarg "two words"') 'Steam %command% arguments passed through unchanged'
    Expect ((Get-Reports).Count -eq 0) 'no crash report for a clean session'
    Expect ($r.Log -match 'refresh switching disabled') 'display untouched when TargetRefreshHz=0'

    Write-Host 'Scenario: crash'
    $r = Invoke-Session 'crash'
    $rep = Get-Reports | Sort-Object Name | Select-Object -Last 1
    Expect ($r.Log -match 'CRASHED, exit code 0xC0000005 \(access violation\)') 'crash detected and decoded'
    Expect ($rep -and (Get-Content -Raw (Join-Path $rep.FullName 'summary.txt')) -match 'access violation') 'crash report summary written'
    Expect ($rep -and (Test-Path (Join-Path $rep.FullName 'hotfix-session.log'))) 'session log attached to the report'

    Write-Host 'Scenario: shutdown hang (window closed, process never exits)'
    $r = Invoke-Session 'hang'
    Expect ($r.Log -match 'Watchdog: terminating KINGDOM HEARTS FINAL MIX\.exe' -and $r.Log -match 'shutdown hang') 'hung shutdown detected and terminated'
    Expect ($r.Seconds -lt 45) "session ended by itself ($($r.Seconds) s)"
    Expect ($r.Log -match 'Backup \[before watchdog termination\]|Backup \[post-exit\]|Backup \[in-game save\]') 'saves backed up around the termination'

    Write-Host 'Scenario: frozen window'
    $r = Invoke-Session 'hungwindow'
    Expect ($r.Log -match 'is not responding' -and $r.Log -match 'frozen') 'frozen window detected and terminated'
    Expect ($r.Seconds -lt 60) "session ended by itself ($($r.Seconds) s)"

    Write-Host 'Scenario: crash while saving, then next launch'
    $null = Invoke-Session 'corruptsave'
    $r = Invoke-Session 'normal'
    Expect ($r.Log -match 'Auto-heal: restored Steam\\76561190000000000\\KHFM_WW\.png') 'damaged save restored before the game started'
    Expect (@(Get-ChildItem (Join-Path $hot 'Backups\_quarantine') -Recurse -Filter *.bin -ErrorAction SilentlyContinue).Count -ge 1) 'damaged copy quarantined'
    Expect ($r.Log -match 'NOTICE: KINGDOM HEARTS save check \(before launch\)') 'user notified before launch'

    Write-Host 'Scenario: game deletes a save mid-session'
    $r = Invoke-Session 'deletesave'
    Expect ($r.Log -match 'Save file disappeared during the session') 'deletion detected'
    Expect ($r.Log -match 'The game deleted Steam\\76561190000000000\\KHFM_WW\.png') 'user told how to get it back'

    Write-Host 'CLI'
    $out = & (Join-Path $hot 'KHHotfixCli.exe') --check | Out-String
    Expect ($LASTEXITCODE -eq 0 -and $out -match 'KHFM_WW\.png' -and $out -match 'Ok') '--check lists the save as Ok'
    $gz = Get-SaveBackups | Sort-Object Name | Select-Object -First 1
    $target = Join-Path $saves 'Steam\76561190000000000\KHFM_WW.png'
    $out = & (Join-Path $hot 'KHHotfixCli.exe') --restore-backup $gz.FullName $target --force | Out-String   # --force: a real game may be running during the test
    Expect ($LASTEXITCODE -eq 0 -and $out -match 'Restored') '--restore-backup restores a chosen version'
}
finally {
    Remove-Item Env:FAKEKH_MODE, Env:KHHOTFIX_SAVES_ROOT, Env:KHHOTFIX_INSTANCE -ErrorAction SilentlyContinue
    Get-Process | Where-Object { try { $_.Path -like "$game\*" } catch { $false } } | ForEach-Object { Stop-Process -Id $_.Id -Force }
}

if ($script:failed -eq 0) { Write-Host "ALL INTEGRATION TESTS PASSED ($work)" -ForegroundColor Green; Remove-Item -Recurse -Force $work; exit 0 }
Write-Host "$script:failed INTEGRATION TEST(S) FAILED - artifacts kept in $work" -ForegroundColor Red
exit 1
