<#
.SYNOPSIS
  Simulates a new user: download the ZIP, extract it with the "downloaded from the internet" mark, run Install.cmd,
  update, then run diagnostics and uninstall from the downloaded folder - all inside %TEMP%. The Steam launch option
  and NVIDIA settings are not touched (-SkipSteam -NoNvidia); the Windows GPU preference is rewritten with the value
  it already has.
.PARAMETER Zip
  ZIP to test. Default: `git archive` of HEAD, which is what GitHub's "Download ZIP" serves.
#>
param([string]$Zip)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ('KHHotfix-install-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
# The downloaded copy's tools hand over to "the installed hotfix": only ever this test's install, never a real one.
$env:KHHOTFIX_INSTALL_DIR = Join-Path $work 'Installed'
$script:failed = 0
function Expect([bool]$c, [string]$w) { if ($c) { Write-Host "  PASS  $w" -ForegroundColor Green } else { Write-Host "  FAIL  $w" -ForegroundColor Red; $script:failed++ } }
function Set-DownloadMark([string]$path) { Set-Content -LiteralPath $path -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3" }
function Test-DownloadMark([string]$path) { [bool](Get-Item -LiteralPath $path -Stream Zone.Identifier -ErrorAction SilentlyContinue) }
function Invoke-Quiet([scriptblock]$native) {
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = (& $native 2>&1 | ForEach-Object { "$_" }) -join "`n"; [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o } }
    finally { $ErrorActionPreference = $eap }
}

. (Join-Path $repo 'KHHotfix.Common.ps1')
$account = Get-SteamAccountId
$launchBefore = if ($account) { Get-KHLaunchOption $account } else { '' }
$gpuKey = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
function Get-KHGpuPrefs {
    $h = @{}
    $k = Get-ItemProperty $gpuKey -ErrorAction SilentlyContinue
    if ($k) { foreach ($p in $k.PSObject.Properties) { if ($p.Name -match 'KINGDOM HEARTS|WaitTitleProject') { $h[$p.Name] = $p.Value } } }
    $h
}
$gpuBefore = Get-KHGpuPrefs

try {
    if (-not $Zip) {
        $Zip = Join-Path $work 'KH-Hotfix-main.zip'
        $r = Invoke-Quiet { git -C $repo archive --format=zip --prefix=KH-Hotfix-main/ -o $Zip HEAD }
        if ($r.Code -ne 0) { throw "git archive failed: $($r.Out)" }
    }
    else { Copy-Item -LiteralPath $Zip -Destination (Join-Path $work 'download.zip'); $Zip = Join-Path $work 'download.zip' }

    Write-Host 'Downloaded ZIP'
    Set-DownloadMark $Zip
    $dl = Join-Path $work 'Downloads'
    Expand-Archive -LiteralPath $Zip -DestinationPath $dl
    $src = (Get-ChildItem -LiteralPath $dl -Directory | Select-Object -First 1).FullName
    $files = @(Get-ChildItem -LiteralPath $src -Recurse -File)
    foreach ($f in $files) { Set-DownloadMark $f.FullName }   # Explorer's "Extract All" passes the mark on to every file
    Expect (-not ($files | Where-Object { $_.Extension -eq '.exe' })) 'no prebuilt executables in the ZIP'
    Expect (-not ($files | Where-Object { $_.FullName -match '\\(Backups|Logs|Reports|State|CrashDumps)\\' })) 'no personal data folders in the ZIP'
    $needles = @($env:USERNAME, $env:COMPUTERNAME, (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).AutoLoginUser)
    if ($account) { $needles += "$account"; $needles += "$($account + $script:SteamId64Base)" }
    $pattern = (@($needles | Where-Object { $_ -and "$_".Length -ge 4 }) | ForEach-Object { [regex]::Escape("$_") }) -join '|'
    $leaks = @($files | Select-String -Pattern $pattern -List)
    Expect ($leaks.Count -eq 0) "no user name, PC name or Steam account of this PC in the ZIP$(if ($leaks) { ': ' + (($leaks | ForEach-Object { $_.Filename }) -join ', ') })"
    $hooks = @($files | Select-String -Pattern 'discord(app)?\.com/api(/v\d+)?/webhooks/\d+/[A-Za-z0-9_-]{20,}' -List)
    Expect ($hooks.Count -eq 0 -and -not ($files | Where-Object { $_.Name -eq 'KHHotfix.local.ini' })) "no Discord webhook URL or KHHotfix.local.ini in the ZIP$(if ($hooks) { ': ' + (($hooks | ForEach-Object { $_.Filename }) -join ', ') })"
    $top = @(Get-ChildItem -LiteralPath $src -Force | ForEach-Object { $_.Name } | Sort-Object)
    $needed = @('Diagnose-KH.ps1', 'Diagnose.cmd', 'Install-KHHotfix.ps1', 'Install.cmd', 'KHHotfix.Common.ps1', 'KHHotfix.ini', 'LICENSE',
                'Restore-KHSave.ps1', 'Restore.cmd', 'src', 'Uninstall-KHHotfix.ps1', 'Uninstall.cmd')
    Expect (@(Compare-Object $top $needed).Count -eq 0) "the ZIP only holds what is needed to install, use and uninstall ($($top -join ', '))"
    $cmds = @($files | Where-Object { $_.Extension -eq '.cmd' })
    $lfOnly = @($cmds | Where-Object { $b = [IO.File]::ReadAllBytes($_.FullName); $bad = $false; for ($i = 0; $i -lt $b.Length; $i++) { if ($b[$i] -eq 10 -and ($i -eq 0 -or $b[$i - 1] -ne 13)) { $bad = $true; break } }; $bad })
    Expect ($cmds.Count -eq 4 -and $lfOnly.Count -eq 0) "all $($cmds.Count) .cmd files have Windows line endings"

    Write-Host 'Install.cmd opened from inside the ZIP'
    $zipView = Join-Path $work 'ZipView'
    New-Item -ItemType Directory -Path $zipView | Out-Null
    Copy-Item -LiteralPath (Join-Path $src 'Install.cmd') -Destination $zipView
    $guard = Join-Path $zipView 'Install.cmd'
    $r = Invoke-Quiet { '' | cmd.exe /c $guard }
    Expect ($r.Code -eq 1 -and $r.Out -match 'Extract All') 'tells the user to extract the ZIP first'

    Write-Host 'First install'
    $inst = Join-Path $work 'Installed'
    $menu = Join-Path $work 'StartMenu'
    # Discord settings shared privately: a KHHotfix.local.ini placed next to Install.cmd (nothing listens on port 9)
    Set-Content -LiteralPath (Join-Path $src 'KHHotfix.local.ini') -Value 'DiscordLogWebhook=http://127.0.0.1:9/api/webhooks/1/secret-token-111' -Encoding ASCII
    $installArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $src 'Install-KHHotfix.ps1'), '-InstallDir', $inst, '-ShortcutRoot', $menu, '-SkipSteam', '-NoNvidia', '-NoClipboard')
    $r = Invoke-Quiet { '' | powershell.exe @installArgs }
    Expect ($r.Code -eq 0) "installer exit code 0 (got $($r.Code))"
    if ($r.Code -ne 0) { Write-Host $r.Out }
    Expect ((Test-Path "$inst\KHHotfix.exe") -and (Test-Path "$inst\KHHotfixCli.exe")) 'built from source in the install folder'
    Expect (-not (Get-ChildItem -LiteralPath $inst -Recurse -File | Where-Object { Test-DownloadMark $_.FullName })) 'installed files no longer carry the downloaded-from-internet mark'
    Expect (-not (Test-Path (Join-Path $inst 'tests'))) 'developer tests are not installed'
    $st = Get-Content -Raw (Join-Path $inst 'State\install.json') | ConvertFrom-Json
    Expect ($st.Managed -eq $true) 'install is recorded as managed'
    $lnks = @(Get-ChildItem -LiteralPath (Join-Path $menu 'KH Hotfix') -Filter *.lnk -ErrorAction SilentlyContinue)
    Expect ($lnks.Count -eq 5) "5 Start menu shortcuts (found $($lnks.Count))"
    $ws = New-Object -ComObject WScript.Shell
    Expect ($ws.CreateShortcut((Join-Path $menu 'KH Hotfix\Restore a KINGDOM HEARTS save.lnk')).TargetPath -eq (Join-Path $inst 'Restore.cmd')) 'Restore shortcut points to the installed copy'
    $bugLnk = $ws.CreateShortcut((Join-Path $menu 'KH Hotfix\Report a KINGDOM HEARTS bug.lnk'))
    Expect ($bugLnk.TargetPath -eq (Join-Path $inst 'KHHotfix.exe') -and $bugLnk.Arguments -eq '--report-bug') 'Report a bug shortcut opens the report window of the installed copy'
    Expect ($r.Out -match ('Installed in ' + [regex]::Escape($inst))) 'installer reports the install folder'
    Expect ($r.Out -match [regex]::Escape('"' + (Join-Path $inst 'KHHotfix.exe') + '" %command%')) 'launch option shown for the installed copy'
    $localIni = Join-Path $inst 'KHHotfix.local.ini'
    Expect ((Test-Path $localIni) -and (Get-Content -Raw $localIni) -match 'DiscordLogWebhook=http://127\.0\.0\.1:9/api/webhooks/1/secret-token-111') 'shared KHHotfix.local.ini settings added to the installation'
    Expect ($r.Out -match 'Settings from KHHotfix\.local\.ini added: DiscordLogWebhook' -and $r.Out -match 'Discord reporting does not work yet' -and $r.Out -notmatch 'secret-token') 'installer checks Discord reporting and never shows the webhook token'
    Expect ((Get-Content (Join-Path $inst 'KHHotfix.ini')) -contains 'TargetRefreshHz=0' -and $r.Out -notmatch '60 FPS|Frame Rate\s*:\s*60') 'no 60 Hz / 60 FPS limit: display left alone, no frame rate advice'

    Write-Host 'Update from a new download'
    $iniPath = Join-Path $inst 'KHHotfix.ini'
    $oldIni = [IO.File]::ReadAllText($iniPath).Replace('; 0 = never touch the display (default).', '; The game is unstable and stutters above 60 Hz/60 FPS; the original refresh rate is restored on exit.')
    [IO.File]::WriteAllText($iniPath, ($oldIni -replace '(?m)^TargetRefreshHz=0(?=\r?$)', 'TargetRefreshHz=60'))   # as installed by 1.3 and older
    Add-Content -LiteralPath $iniPath -Value '; my own setting'
    Add-Content -LiteralPath $localIni -Value 'DetailedMessages=1'
    Set-Content -LiteralPath (Join-Path $src 'KHHotfix.local.ini') -Encoding ASCII -Value @('DiscordLogWebhook=http://127.0.0.1:9/api/webhooks/1/secret-token-111', 'DiscordBugReportWebhook=http://127.0.0.1:9/api/webhooks/2/secret-token-222')
    $exeTime = (Get-Item "$inst\KHHotfix.exe").LastWriteTimeUtc
    Start-Sleep -Seconds 1
    $r = Invoke-Quiet { '' | powershell.exe @installArgs }
    Expect ($r.Code -eq 0) "second install exit code 0 (got $($r.Code))"
    Expect ((Get-Content -Raw $iniPath) -match 'my own setting') 'KHHotfix.ini settings kept'
    $ini = Get-Content -Raw $iniPath
    Expect ($ini -match '(?m)^TargetRefreshHz=0\r?$' -and $ini -notmatch 'unstable and stutters' -and $r.Out -match 'The 60 Hz limit while playing is removed') 'old 60 Hz default of an earlier install removed'
    Expect ((Get-Item "$inst\KHHotfix.exe").LastWriteTimeUtc -gt $exeTime) 'binaries rebuilt'
    $local = @(Get-Content $localIni)
    Expect ($local -contains 'DetailedMessages=1' -and @($local -match '^DiscordLogWebhook=').Count -eq 1 -and $local -contains 'DiscordBugReportWebhook=http://127.0.0.1:9/api/webhooks/2/secret-token-222' -and $r.Out -match '(?m)Settings from KHHotfix\.local\.ini added: DiscordBugReportWebhook\r?$') 'update: own KHHotfix.local.ini settings kept, new shared settings added once'

    Write-Host 'Diagnostics, started from the downloaded folder'
    $r = Invoke-Quiet { '' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'Diagnose-KH.ps1') }
    Expect ($r.Code -eq 0 -and @(Get-ChildItem (Join-Path $inst 'Reports') -Filter 'diagnose-*.txt' -ErrorAction SilentlyContinue).Count -eq 1 -and -not (Test-Path (Join-Path $src 'Reports'))) 'runs the installed copy (report written there)'

    Write-Host 'Uninstall, started from the downloaded folder, deleting everything'
    $p = Start-Process -FilePath cmd.exe -ArgumentList "/c powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$src\Uninstall-KHHotfix.ps1`" -RemoveData > `"$work\uninstall.txt`" 2>&1" -Wait -PassThru -WindowStyle Hidden
    $out = Get-Content -Raw "$work\uninstall.txt"
    Expect ($p.ExitCode -eq 0) "uninstaller exit code 0 (got $($p.ExitCode))"
    if ($p.ExitCode -ne 0) { Write-Host $out }
    Expect ($out -match ('Uninstalling the hotfix installed in ' + [regex]::Escape($inst)) -and $out -match 'Uninstall finished') 'the downloaded Uninstall.cmd uninstalls the installed copy'
    Expect (-not (Test-Path (Join-Path $menu 'KH Hotfix'))) 'Start menu folder removed'
    for ($i = 0; $i -lt 40 -and (Test-Path $inst); $i++) { Start-Sleep -Seconds 1 }
    Expect (-not (Test-Path $inst)) 'install folder deleted once the uninstaller window closed'

    Write-Host 'Nothing outside the test changed'
    Expect ((Get-KHLaunchOption $account) -eq $launchBefore) 'Steam launch option unchanged'
    $gpuAfter = Get-KHGpuPrefs
    Expect ($gpuAfter.Count -eq $gpuBefore.Count -and -not ($gpuBefore.Keys | Where-Object { $gpuAfter[$_] -ne $gpuBefore[$_] })) 'Windows GPU preferences unchanged'
}
finally {
    Remove-Item Env:KHHOTFIX_INSTALL_DIR -ErrorAction SilentlyContinue
    Get-Process | Where-Object { try { $_.Path -like "$work\*" } catch { $false } } | ForEach-Object { Stop-Process -Id $_.Id -Force }
}

if ($script:failed -eq 0) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue; Write-Host 'ALL INSTALL TESTS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$script:failed INSTALL TEST(S) FAILED - files kept in $work" -ForegroundColor Red
exit 1
