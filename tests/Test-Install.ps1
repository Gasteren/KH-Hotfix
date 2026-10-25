<#
.SYNOPSIS
  Simulates a new user: download the ZIP, extract it with the "downloaded from the internet" mark, run Install.cmd,
  update, run diagnostics and uninstall - all inside %TEMP%. The Steam launch option and NVIDIA settings are not
  touched (-SkipSteam -NoNvidia); the Windows GPU preference is rewritten with the value it already has.
.PARAMETER Zip
  ZIP to test. Default: `git archive` of HEAD, which is what GitHub's "Download ZIP" serves.
#>
param([string]$Zip)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP ('KHHotfix-install-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
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
    $cmds = @($files | Where-Object { $_.Extension -eq '.cmd' })
    $lfOnly = @($cmds | Where-Object { $b = [IO.File]::ReadAllBytes($_.FullName); $bad = $false; for ($i = 0; $i -lt $b.Length; $i++) { if ($b[$i] -eq 10 -and ($i -eq 0 -or $b[$i - 1] -ne 13)) { $bad = $true; break } }; $bad })
    Expect ($cmds.Count -ge 5 -and $lfOnly.Count -eq 0) "all $($cmds.Count) .cmd files have Windows line endings"

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
    Expect ($lnks.Count -eq 4) "4 Start menu shortcuts (found $($lnks.Count))"
    $ws = New-Object -ComObject WScript.Shell
    Expect ($ws.CreateShortcut((Join-Path $menu 'KH Hotfix\Restore a KINGDOM HEARTS save.lnk')).TargetPath -eq (Join-Path $inst 'Restore.cmd')) 'Restore shortcut points to the installed copy'
    Expect ($r.Out -match ('Installed in ' + [regex]::Escape($inst))) 'installer reports the install folder'
    Expect ($r.Out -match [regex]::Escape('"' + (Join-Path $inst 'KHHotfix.exe') + '" %command%')) 'launch option shown for the installed copy'

    Write-Host 'Update from a new download'
    Add-Content -LiteralPath (Join-Path $inst 'KHHotfix.ini') -Value '; my own setting'
    $exeTime = (Get-Item "$inst\KHHotfix.exe").LastWriteTimeUtc
    Start-Sleep -Seconds 1
    $r = Invoke-Quiet { '' | powershell.exe @installArgs }
    Expect ($r.Code -eq 0) "second install exit code 0 (got $($r.Code))"
    Expect ((Get-Content -Raw (Join-Path $inst 'KHHotfix.ini')) -match 'my own setting') 'KHHotfix.ini settings kept'
    Expect ((Get-Item "$inst\KHHotfix.exe").LastWriteTimeUtc -gt $exeTime) 'binaries rebuilt'

    Write-Host 'Diagnostics from the installed copy'
    $r = Invoke-Quiet { '' | powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $inst 'Diagnose-KH.ps1') }
    Expect ($r.Code -eq 0 -and @(Get-ChildItem (Join-Path $inst 'Reports') -Filter 'diagnose-*.txt' -ErrorAction SilentlyContinue).Count -eq 1) 'diagnostics report written'

    Write-Host 'Uninstall, deleting everything'
    $p = Start-Process -FilePath cmd.exe -ArgumentList "/c powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$inst\Uninstall-KHHotfix.ps1`" -RemoveData > `"$work\uninstall.txt`" 2>&1" -Wait -PassThru -WindowStyle Hidden
    $out = Get-Content -Raw "$work\uninstall.txt"
    Expect ($p.ExitCode -eq 0) "uninstaller exit code 0 (got $($p.ExitCode))"
    if ($p.ExitCode -ne 0) { Write-Host $out }
    Expect (-not (Test-Path (Join-Path $menu 'KH Hotfix'))) 'Start menu folder removed'
    for ($i = 0; $i -lt 40 -and (Test-Path $inst); $i++) { Start-Sleep -Seconds 1 }
    Expect (-not (Test-Path $inst)) 'install folder deleted once the uninstaller window closed'

    Write-Host 'Nothing outside the test changed'
    Expect ((Get-KHLaunchOption $account) -eq $launchBefore) 'Steam launch option unchanged'
    $gpuAfter = Get-KHGpuPrefs
    Expect ($gpuAfter.Count -eq $gpuBefore.Count -and -not ($gpuBefore.Keys | Where-Object { $gpuAfter[$_] -ne $gpuBefore[$_] })) 'Windows GPU preferences unchanged'
}
finally {
    Get-Process | Where-Object { try { $_.Path -like "$work\*" } catch { $false } } | ForEach-Object { Stop-Process -Id $_.Id -Force }
}

if ($script:failed -eq 0) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue; Write-Host 'ALL INSTALL TESTS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$script:failed INSTALL TEST(S) FAILED - files kept in $work" -ForegroundColor Red
exit 1
