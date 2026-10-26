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
Copy-Item "$work\FakeKH.exe" (Join-Path $game 'WaitTitleProject.exe')
. (Join-Path $root 'KHHotfix.Common.ps1')
Invoke-HotfixBuild $root $hot   # fresh build from src\ (never uses or locks the binaries of a running hotfix)
$env:KHHOTFIX_INSTANCE = '.test'  # separate session lock, so the tests also run while a real game session is active

# Local fake Discord / report relay: /log behaves like a forum channel, /bug like a text channel (see FakeKH.cs).
$discord = Join-Path $work 'discord'
$fakeDiscord = Start-Process -FilePath "$work\FakeKH.exe" -ArgumentList @('--fake-discord', ('"' + $discord + '"')) -PassThru
for ($i = 0; $i -lt 100 -and -not (Test-Path (Join-Path $discord 'port.txt')); $i++) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path (Join-Path $discord 'port.txt'))) { throw 'fake Discord server did not start' }
$port = (Get-Content -Raw (Join-Path $discord 'port.txt')).Trim()
@"
TargetRefreshHz=0
ShowDialogs=0
QuitHangKillSeconds=5
HungWindowKillSeconds=6
ExitGraceSeconds=3
WaitScreenCloseSeconds=4
PanicHotkey=Ctrl+Alt+Shift+F11
BugReportHotkey=Ctrl+Alt+Shift+F10
ReportRelay=http://127.0.0.1:$port
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
function Get-Reports { @(Get-ChildItem (Join-Path $hot 'Reports') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike 'bug-*' }) }
function Get-SaveBackups { @(Get-ChildItem (Join-Path $hot 'Backups\Steam\76561190000000000\KHFM_WW.png') -Filter *.gz -ErrorAction SilentlyContinue) }
function Get-Posts { @(Get-ChildItem $discord -Filter 'post-*.json' -ErrorAction SilentlyContinue | Sort-Object { [int]($_.BaseName -replace '\D', '') }) }
function Read-Post($file) {
    if (-not $file) { return $null }
    $raw = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
    [pscustomobject]@{
        Raw   = $raw
        Json  = ($raw | ConvertFrom-Json)
        Files = @(Get-Content ($file.FullName -replace '\.json$', '.files') | ForEach-Object { ($_ -split ' ')[0] })
        Hook  = (Get-Content -Raw ($file.FullName -replace '\.json$', '.hook')).Trim()
    }
}
function Get-NewPost([int]$before) { $p = Get-Posts; if ($p.Count -eq $before + 1) { Read-Post $p[-1] } }

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
    Expect ($r.Log -match 'Audio outputs enabled: \d+') 'audio outputs checked before launch'
    Expect ($r.Log -match 'loading guard not available: .*different game version') 'loading guard backs off on an unknown KH1 build (writes nothing)'
    Expect ($r.Log -match 'Discord: bug reports relay 127\.0\.0\.1:\d+/bug, crash and session reports relay 127\.0\.0\.1:\d+/log') 'reports go to the maintainer through the report relay'
    Expect ($r.Log -match 'Bug report hotkey active: Ctrl\+Alt\+Shift\+F10') 'bug report hotkey registered'
    Expect ((Get-Posts).Count -eq 0) 'nothing is posted to Discord for a session without problems'

    Write-Host 'Scenario: crash'
    $posts = (Get-Posts).Count
    $r = Invoke-Session 'crash'
    $rep = Get-Reports | Sort-Object Name | Select-Object -Last 1
    Expect ($r.Log -match 'CRASHED, exit code 0xC0000005 \(access violation\)') 'crash detected and decoded'
    Expect ($rep -and (Get-Content -Raw (Join-Path $rep.FullName 'summary.txt')) -match 'access violation') 'crash report summary written'
    Expect ($rep -and (Test-Path (Join-Path $rep.FullName 'hotfix-session.log'))) 'session log attached to the report'
    $post = Get-NewPost $posts
    Expect ($post -and $post.Hook -eq '1' -and $post.Json.thread_name -match '^\[KH1\] KINGDOM HEARTS FINAL MIX crashed: 0xC0000005 \(access violation\) - ') 'crash posted to the log webhook automatically, as a titled forum post'
    Expect ($post -and $post.Files -contains 'summary.txt' -and $post.Files -contains 'session.log' -and $post.Files -contains 'system.txt' -and $post.Json.embeds[0].title -eq 'Crash report' -and $post.Raw -match '"allowed_mentions":\{"parse":\[\]\}') 'crash post: summary, session log, system info, crash embed, no pings'
    Expect ($rep -and (Test-Path (Join-Path $rep.FullName 'discord-sent.txt')) -and $r.Log -match 'Report sent to Discord \(relay 127\.0\.0\.1:\d+/log\)') 'crash report marked as sent'
    Expect ($r.Log -notmatch 'NOTICE: KINGDOM HEARTS session summary' -and $r.Log -match 'Not shown to the player \(DetailedMessages=0\): .*crashed\. Your saves were backed up') 'crash reported automatically: no session summary dialog for the player'

    Write-Host 'Scenario: shutdown hang (window closed, process never exits)'
    $posts = (Get-Posts).Count
    $r = Invoke-Session 'hang'
    Expect ($r.Log -match 'Watchdog: terminating KINGDOM HEARTS FINAL MIX\.exe' -and $r.Log -match 'shutdown hang') 'hung shutdown detected and terminated'
    Expect ($r.Seconds -lt 45) "session ended by itself ($($r.Seconds) s)"
    Expect ($r.Log -match 'Backup \[before watchdog termination\]|Backup \[post-exit\]|Backup \[in-game save\]') 'saves backed up around the termination'
    $rep = Get-Reports | Sort-Object Name | Select-Object -Last 1
    Expect ($rep -and @(Get-ChildItem $rep.FullName -Filter '*frozen*.dmp').Count -eq 1 -and (Get-Content -Raw (Join-Path $rep.FullName 'summary.txt')) -match 'Freeze analysis') 'freeze snapshot taken before closing and analyzed in the report'
    $post = Get-NewPost $posts
    Expect ($post -and $post.Json.thread_name -match '^\[KH1\] KINGDOM HEARTS FINAL MIX ' -and $post.Json.embeds[0].title -eq 'Freeze report' -and @($post.Files -match 'frozen.*\.dmp\.gz$').Count -eq 1) 'freeze posted with the packed snapshot'
    Expect ($r.Log -notmatch 'NOTICE: KINGDOM HEARTS session summary' -and $post -and $post.Json.content -match 'The hotfix closed it for you') 'freeze handled by the hotfix: only in the report, not shown to the player'

    Write-Host 'Scenario: frozen window'
    $r = Invoke-Session 'hungwindow'
    Expect ($r.Log -match 'is not responding' -and $r.Log -match 'frozen') 'frozen window detected and terminated'
    Expect ($r.Seconds -lt 60) "session ended by itself ($($r.Seconds) s)"
    $rep = Get-Reports | Sort-Object Name | Select-Object -Last 1
    Expect ($rep -and @(Get-ChildItem $rep.FullName -Filter '*frozen*.dmp').Count -eq 1 -and (Get-Content -Raw (Join-Path $rep.FullName 'summary.txt')) -match 'Freeze analysis') 'frozen window: snapshot taken and analyzed'

    Write-Host 'Scenario: game ends on its way back to its title screen (loading screen left behind)'
    $posts = (Get-Posts).Count
    $r = Invoke-Session 'menustuck'
    Expect ($r.Log -match 'Tracking WaitTitleProject\.exe' -and $r.Log -match 'KINGDOM HEARTS FINAL MIX\.exe \(pid \d+\) exited, code 0x00000001') 'game went back to its title screen (exit code 1) and its loading screen was tracked'
    Expect ($r.Log -match 'WaitTitleProject\.exe \(pid \d+\) was terminated by the hotfix \(was left on screen for \d+ s after the game ended') 'loading screen left behind is closed'
    Expect ($r.Seconds -lt 45) "session ended by itself ($($r.Seconds) s)"
    Expect ($r.Log -match 'NOTICE: KINGDOM HEARTS session summary: .*did not get back to its title screen or the collection menu') 'player told to start the game again from Steam'
    $post = Get-NewPost $posts
    Expect ($post -and $post.Hook -eq '1' -and $post.Json.thread_name -match '^\[KH1\] WaitTitleProject was left on screen') 'reported to the maintainer'

    Write-Host 'Scenario: crash while saving, then next launch'
    $null = Invoke-Session 'corruptsave'
    $r = Invoke-Session 'normal'
    Expect ($r.Log -match 'Auto-heal: restored Steam\\76561190000000000\\KHFM_WW\.png') 'damaged save restored before the game started'
    Expect (@(Get-ChildItem (Join-Path $hot 'Backups\_quarantine') -Recurse -Filter *.bin -ErrorAction SilentlyContinue).Count -ge 1) 'damaged copy quarantined'
    Expect ($r.Log -match 'NOTICE: KINGDOM HEARTS check \(before launch\)') 'user notified before launch'

    Write-Host 'Scenario: game deletes a save mid-session'
    $posts = (Get-Posts).Count
    $r = Invoke-Session 'deletesave'
    Expect ($r.Log -match 'Save file disappeared during the session') 'deletion detected'
    Expect ($r.Log -match 'The game deleted Steam\\76561190000000000\\KHFM_WW\.png') 'user told how to get it back'
    $post = Get-NewPost $posts
    Expect ($post -and $post.Json.thread_name -match '^\[KH1\] The game deleted Steam\\<steamid>\\KHFM_WW\.png' -and $post.Json.embeds[0].title -eq 'Session report' -and $post.Raw -notmatch '76561190000000000') 'session with a deleted save reported, Steam id removed'
    Expect ($r.Log -match 'NOTICE: KINGDOM HEARTS session summary: .*The game deleted Steam') 'a deleted save is shown to the player'

    Write-Host 'CLI'
    $out = & (Join-Path $hot 'KHHotfixCli.exe') --check | Out-String
    Expect ($LASTEXITCODE -eq 0 -and $out -match 'KHFM_WW\.png' -and $out -match 'Ok') '--check lists the save as Ok'
    $gz = Get-SaveBackups | Sort-Object Name | Select-Object -First 1
    $target = Join-Path $saves 'Steam\76561190000000000\KHFM_WW.png'
    $out = & (Join-Path $hot 'KHHotfixCli.exe') --restore-backup $gz.FullName $target --force | Out-String   # --force: a real game may be running during the test
    Expect ($LASTEXITCODE -eq 0 -and $out -match 'Restored') '--restore-backup restores a chosen version'

    Write-Host 'Bug report from the command line (fake game window, text-channel webhook)'
    $cli = Join-Path $hot 'KHHotfixCli.exe'
    $out = & $cli --discord-test | Out-String
    Expect ($LASTEXITCODE -eq 0 -and $out -match 'Bug reports: relay 127\.0\.0\.1:\d+/bug OK' -and $out -match 'Crash and session reports: relay 127\.0\.0\.1:\d+/log OK .*forum channel') '--discord-test checks both relay endpoints (nothing posted)'
    $posts = (Get-Posts).Count
    $out = & $cli --discord-test --send | Out-String
    $code = $LASTEXITCODE
    $new = @(Get-Posts | Select-Object -Skip $posts | ForEach-Object { Read-Post $_ })
    Expect ($code -eq 0 -and $new.Count -eq 2 -and @($new | Where-Object { $_.Hook -eq '2' -and $_.Files -contains 'screenshot.jpg' -and $_.Json.embeds[0].title -eq 'Test - bug reports arrive here' }).Count -eq 1 -and @($new | Where-Object { $_.Hook -eq '1' -and $_.Json.thread_name -match '^\[KH\] Test - crash and session reports arrive here' }).Count -eq 1) '--discord-test --send posts a sample report to each webhook'
    $bugs = @(Get-ChildItem (Join-Path $hot 'Reports') -Directory -Filter 'bug-*' -ErrorAction SilentlyContinue).Count
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & $cli --report-bug --send 2>&1 | Out-Null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $eap
    Expect ($code -eq 2 -and @(Get-ChildItem (Join-Path $hot 'Reports') -Directory -Filter 'bug-*' -ErrorAction SilentlyContinue).Count -eq $bugs) '--report-bug --send without text: usage error, nothing captured'
    $env:FAKEKH_MODE = 'idle'
    $fakeGame = Start-Process -FilePath (Join-Path $game 'KINGDOM HEARTS FINAL MIX.exe') -PassThru
    Start-Sleep -Seconds 2
    $posts = (Get-Posts).Count
    $out = & $cli --report-bug --send --pid $fakeGame.Id --title 'Stuck after a cutscene' --text 'Line one\nLine two <@123> @everyone' --game KH1 --category Sound --name Tester | Out-String
    $code = $LASTEXITCODE
    Stop-Process -Id $fakeGame.Id -Force
    $post = Get-NewPost $posts
    Expect ($code -eq 0 -and $out -match 'Sent') "--report-bug --send exits 0 ($($out.Trim()))"
    Expect ($post -and $post.Hook -eq '2' -and -not $post.Json.thread_name -and $post.Json.content -eq "Line one`nLine two <@123> @everyone" -and $post.Json.username -eq 'Tester (KH Hotfix)') 'sent to the text-channel webhook as a normal message; text and name intact'
    Expect ($post -and $post.Files -contains 'screenshot.jpg' -and $post.Json.embeds[0].image.url -eq 'attachment://screenshot.jpg' -and $post.Files -contains 'game-snapshot.dmp.gz' -and $post.Files -contains 'system.txt' -and $post.Files -contains 'session.log') 'screenshot of the game window (shown in the post), snapshot, system info and log attached'
    Expect ($post -and ($post.Json.embeds[0].fields | Where-Object { $_.name -eq 'Problem' }).value -eq 'Sound' -and ($post.Json.embeds[0].fields | Where-Object { $_.name -eq 'Game' }).value -eq 'KINGDOM HEARTS FINAL MIX') 'game and problem resolved from the short names'
    $bug = Get-ChildItem (Join-Path $hot 'Reports') -Directory -Filter 'bug-*' | Sort-Object Name | Select-Object -Last 1
    Expect ($bug -and (Test-Path (Join-Path $bug.FullName 'report.txt')) -and (Test-Path (Join-Path $bug.FullName 'discord-sent.txt')) -and (Get-Content -Raw (Join-Path $bug.FullName 'meta.txt')) -match 'GameResponding=yes') 'bug report folder kept with report.txt and marked as sent'
    Expect (-not (Test-Path (Join-Path $discord 'errors.txt'))) 'fake Discord saw only well-formed requests'

    $kh = Find-KHInstall
    if ($kh) {
        Write-Host 'Installed game (read-only checks)'
        $out = & (Join-Path $hot 'KHHotfixCli.exe') --fixes-scan $kh.Path | Out-String
        Expect ($out -match 'KINGDOM HEARTS FINAL MIX\.exe\s+Endless loading screen\s+ready') 'endless-loading guard matches the installed KH1'
        Expect ($out -match 'Endless loading screen\s+ready \(.*audio-failure flag RVA 0x[0-9A-F]+') 'audio-failure flag found in the installed KH1'
        foreach ($g in 'KINGDOM HEARTS FINAL MIX', 'KINGDOM HEARTS II FINAL MIX', 'KINGDOM HEARTS Birth by Sleep FINAL MIX', 'KINGDOM HEARTS Re_Chain of Memories', 'KINGDOM HEARTS Theater') {
            Expect ($out -match ([regex]::Escape("$g.exe") + '\s+Quit Game crash/freeze\s+ready \(RVA 0x[0-9A-F]+, exit code hand-over at RVA 0x[0-9A-F]+')) "quit fix with the exit code hand-over matches the installed $g"
        }
    }
    else { Write-Host '  SKIP  game not installed - real-executable checks skipped' }
}
finally {
    Remove-Item Env:FAKEKH_MODE, Env:KHHOTFIX_SAVES_ROOT, Env:KHHOTFIX_INSTANCE -ErrorAction SilentlyContinue
    Get-Process | Where-Object { try { $_.Path -like "$game\*" } catch { $false } } | ForEach-Object { Stop-Process -Id $_.Id -Force }
    New-Item -ItemType File -Force -Path (Join-Path $discord 'stop.txt') | Out-Null
    if ($fakeDiscord -and -not $fakeDiscord.WaitForExit(3000)) { Stop-Process -Id $fakeDiscord.Id -Force }
}

if ($script:failed -eq 0) { Write-Host "ALL INTEGRATION TESTS PASSED ($work)" -ForegroundColor Green; Remove-Item -Recurse -Force $work; exit 0 }
Write-Host "$script:failed INTEGRATION TEST(S) FAILED - artifacts kept in $work" -ForegroundColor Red
exit 1
