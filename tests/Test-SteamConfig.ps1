# Tests the Steam config helpers on temporary files (never on the live Steam config).
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'KHHotfix.Common.ps1')
$tmp = Join-Path $env:TEMP ('KHHotfix-vdf-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$script:StateDir = Join-Path $tmp 'State'
function Get-LocalConfigPath([int64]$accountId) { Join-Path $tmp "$accountId.vdf" }
$failed = 0
function Expect([bool]$c, [string]$w) { if ($c) { Write-Host "  PASS  $w" -ForegroundColor Green } else { Write-Host "  FAIL  $w" -ForegroundColor Red; $script:failed++ } }

$sample = @"
"UserLocalConfigStore"
{
`t"Software"
`t{
`t`t"Valve"
`t`t{
`t`t`t"Steam"
`t`t`t{
`t`t`t`t"apps"
`t`t`t`t{
`t`t`t`t`t"2552430"
`t`t`t`t`t{
`t`t`t`t`t`t"LastPlayed"`t`t"1792886756"
`t`t`t`t`t`t"cloud"
`t`t`t`t`t`t{
`t`t`t`t`t`t`t"last_sync_state"`t`t"synchronized"
`t`t`t`t`t`t}
`t`t`t`t`t}
`t`t`t`t`t"10"
`t`t`t`t`t{
`t`t`t`t`t`t"LaunchOptions"`t`t"-novid"
`t`t`t`t`t}
`t`t`t`t}
`t`t`t}
`t`t}
`t}
}
"@
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Get-LocalConfigPath 1), $sample, $utf8)
$value = '"C:\Users\Some One\KH-Hotfix\KHHotfix.exe" %command% -reboot=true'

Write-Host 'Writing localconfig.vdf'
$prev = Set-KHLaunchOptionText 1 $value
Expect ($prev -eq '') 'previous value empty'
Expect ((Get-KHLaunchOption 1) -eq $value) 'value reads back exactly (quotes and backslashes escaped)'
$text = [IO.File]::ReadAllText((Get-LocalConfigPath 1))
Expect ($text -match '"LaunchOptions"\t\t"\\"C:\\\\Users\\\\Some One\\\\KH-Hotfix\\\\KHHotfix\.exe\\" %command% -reboot=true"') 'stored in Steam escape format'
Expect ($text -match '"10"\s*\{\s*"LaunchOptions"\s*"-novid"') 'other games untouched'
Expect ([IO.File]::ReadAllBytes((Get-LocalConfigPath 1))[0] -eq 0x22) 'no UTF-8 BOM written'
$prev = Set-KHLaunchOptionText 1 '-windowed'
Expect ($prev -eq $value -and (Get-KHLaunchOption 1) -eq '-windowed') 'existing value returned and replaced'
Expect (([regex]::Matches([IO.File]::ReadAllText((Get-LocalConfigPath 1)), '"LaunchOptions"')).Count -eq 2) 'no duplicate keys'
[IO.File]::WriteAllText((Get-LocalConfigPath 2), ($sample -replace '"2552430"', '"999"'), $utf8)
$null = Set-KHLaunchOptionText 2 $value
Expect ((Get-KHLaunchOption 2) -eq $value) 'missing app block is created under apps'

Write-Host 'Composing the launch option'
$ours = '"' + $script:Wrapper + '" %command%'
Expect ((Get-WrapperLaunchOption '') -eq $ours) 'empty -> hotfix only'
Expect ((Get-WrapperLaunchOption '-reboot=true') -eq "$ours -reboot=true") 'plain arguments kept after %command%'
Expect ((Get-WrapperLaunchOption "$ours -x") -eq "$ours -x") 'already set -> unchanged'
Expect ((Get-WrapperLaunchOption '"D:\Old Place\KHHotfix.exe" %command% -x') -eq "$ours -x") 'hotfix from another folder is replaced, arguments kept'
Expect ((Get-WrapperLaunchOption 'tool.exe %command% -x') -eq "tool.exe $ours -x") 'another launch tool is kept and wraps the hotfix'
Expect ((Test-OwnLaunchOption "$ours -x") -and -not (Test-OwnLaunchOption '"D:\Other\KHHotfix.exe" %command%') -and -not (Test-OwnLaunchOption '')) 'own option detected only for this folder'

Write-Host 'Removing the launch option'
Expect ((Remove-WrapperLaunchOption $ours) -eq '') 'hotfix only -> empty'
Expect ((Remove-WrapperLaunchOption "$ours -reboot=true") -eq '-reboot=true') 'other arguments kept'
Expect ((Remove-WrapperLaunchOption "tool.exe $ours -x") -eq 'tool.exe %command% -x') 'other launch tool restored'
Expect ((Remove-WrapperLaunchOption '"D:\Other\KHHotfix.exe" %command%') -eq '"D:\Other\KHHotfix.exe" %command%') 'another copy of the hotfix is left alone'

Write-Host 'Choosing the Steam account when Steam is closed'
$modern = @"
"users"
{
`t"76561190000000001"
`t{
`t`t"AccountName"`t`t"alice"
`t`t"AutoLogin"`t`t"1"
`t`t"timestamp"`t`t"1700000000"
`t}
`t"76561190000000002"
`t{
`t`t"AccountName"`t`t"bob"
`t`t"timestamp"`t`t"1800000000"
`t}
}
"@
$old = $modern.Replace("`"AccountName`"`t`t`"alice`"", "`"AccountName`"`t`t`"alice`"`n`t`t`"MostRecent`"`t`t`"1`"")
$id1 = [int64]76561190000000001 - $script:SteamId64Base
$id2 = [int64]76561190000000002 - $script:SteamId64Base
Expect ((Select-SteamAccount $old $null $null) -eq $id1) 'MostRecent (older Steam) wins'
Expect ((Select-SteamAccount $modern 'alice' $null) -eq $id1) 'auto-login account (current Steam has no MostRecent)'
Expect ((Select-SteamAccount $modern $null $null) -eq $id2) 'otherwise the newest timestamp'
Expect ((Select-SteamAccount $modern $null { param($id) $id -eq $id1 }) -eq $id1) 'accounts without a Steam config are skipped'
Expect ($null -ne (Get-SteamAccountId)) 'an account is found on this PC'

$acct = Get-SteamAccountId
$real = Join-Path (Get-SteamPath) "userdata\$acct\config\localconfig.vdf"
if ($acct -and (Test-Path -LiteralPath $real)) {
    Write-Host 'Copy of the real localconfig.vdf'
    Copy-Item -LiteralPath $real -Destination (Get-LocalConfigPath 3)
    $before = [IO.File]::ReadAllText((Get-LocalConfigPath 3))
    $orig = Get-KHLaunchOption 3
    $null = Set-KHLaunchOptionText 3 $value
    $after = [IO.File]::ReadAllText((Get-LocalConfigPath 3))
    Expect ((Get-KHLaunchOption 3) -eq $value) 'value written and read back'
    if ($orig) {
        $null = Set-KHLaunchOptionText 3 $orig
        Expect ([IO.File]::ReadAllText((Get-LocalConfigPath 3)) -eq $before) 'writing the original value back gives a byte-identical file'
    }
    else {
        $restored = [regex]::Replace($after, '\n\t*"LaunchOptions"\t\t"(?:\\.|[^"\\])*"', '', 1)
        Expect ($restored -eq $before) 'only one line added, everything else byte-identical'
    }
}

Remove-Item -Recurse -Force $tmp
if ($failed -eq 0) { Write-Host 'ALL STEAM CONFIG TESTS PASSED' -ForegroundColor Green; exit 0 }
Write-Host "$failed STEAM CONFIG TEST(S) FAILED" -ForegroundColor Red; exit 1
