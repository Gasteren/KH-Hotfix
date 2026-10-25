<#
.SYNOPSIS
  Deploys the KH Hotfix report relay (worker.js) to your Cloudflare account - the free Workers plan is enough - and
  stores your Discord webhooks in it as secrets, so every copy of the hotfix can report to your Discord without the
  webhook URLs ever being published. Run it again after changing a webhook.
.DESCRIPTION
  Takes the webhooks from DiscordBugReportWebhook and DiscordLogWebhook in ..\KHHotfix.local.ini (or asks for them),
  logs in to Cloudflare in your browser when needed, deploys the Worker, writes its address into src\Program.cs
  (ReportRelay, which every build of the hotfix uses), checks it with a fresh build of the hotfix, and offers to
  commit and push. Needs Node.js.
#>
$ErrorActionPreference = 'Continue'   # wrangler reports progress on stderr
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $PSScriptRoot
$wrangler = @('--yes', 'wrangler@4')

function Step([string]$t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Fail([string]$t) { Write-Host "   [FAILED] $t" -ForegroundColor Red; exit 1 }
function Invoke-Wrangler([string[]]$arguments) {
    $lines = & npx @wrangler @arguments 2>&1 | ForEach-Object { $s = "$_"; Write-Host "   $s"; $s }
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($lines -join "`n") }
}
function Get-Webhook([string]$key, [string]$what) {
    $ini = Join-Path $root 'KHHotfix.local.ini'
    if (Test-Path -LiteralPath $ini) {
        foreach ($l in Get-Content -LiteralPath $ini) { if ($l -match "^\s*$key\s*=\s*(\S+)\s*$") { return $Matches[1].Trim('"', "'") } }
    }
    (Read-Host "   Discord webhook URL for $what").Trim()
}

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) { Fail 'Node.js is needed: install it from https://nodejs.org and run this again.' }

Step 'Discord webhooks'
$hooks = [ordered]@{
    BUG_WEBHOOK = Get-Webhook 'DiscordBugReportWebhook' 'bug reports (a forum channel works best)'
    LOG_WEBHOOK = Get-Webhook 'DiscordLogWebhook' 'crash and session reports'
}
foreach ($k in @($hooks.Keys)) {
    if ($hooks[$k] -notmatch '^https://(?:(?:ptb|canary)\.)?discord(?:app)?\.com/api(?:/v\d+)?/webhooks/(\d+)/[\w-]{20,}$') { Fail "$k is not a Discord webhook URL" }
    Write-Host "   $k = webhook $($Matches[1])"
}

Step 'Cloudflare login'
$who = Invoke-Wrangler @('whoami')
if ($who.Text -notmatch 'associated with the email|You are logged in') {
    Write-Host '   Your browser opens: log in to Cloudflare (or create a free account) and allow access.'
    $login = Invoke-Wrangler @('login')
    if ($login.Code -ne 0) { Fail 'Cloudflare login did not complete.' }
}

Step 'Deploying the relay'
$deploy = Invoke-Wrangler @('deploy')
if ($deploy.Code -ne 0 -and $deploy.Text -match 'workers\.dev subdomain') {
    Write-Host '   Your Cloudflare account needs a workers.dev subdomain (part of the relay address) - choose one when asked.' -ForegroundColor Yellow
    & npx @wrangler deploy   # interactive, so wrangler can ask for the subdomain
    $deploy = Invoke-Wrangler @('deploy')
}
if ($deploy.Code -ne 0) { Fail 'wrangler deploy failed (see above).' }
$url = [regex]::Match($deploy.Text, 'https://kh-hotfix-reports\.[a-z0-9-]+\.workers\.dev').Value
if (-not $url) { $url = (Read-Host '   Paste the https://kh-hotfix-reports....workers.dev address shown above').Trim().TrimEnd('/') }
if ($url -notmatch '^https://[a-z0-9.-]+$') { Fail "Not a relay address: $url" }

Step 'Storing the webhooks in the relay (as secrets)'
foreach ($k in @($hooks.Keys)) {
    $lines = $hooks[$k] | & npx @wrangler secret put $k 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { $lines | ForEach-Object { Write-Host "   $_" }; Fail "Storing $k failed." }
    Write-Host "   $k stored"
}

Step 'Checking the relay'
foreach ($ep in 'bug', 'log') {
    $ok = $false
    for ($i = 0; $i -lt 12 -and -not $ok; $i++) {
        try {
            $r = Invoke-RestMethod -Uri "$url/$ep" -UserAgent 'KH-Hotfix (relay deploy check)' -TimeoutSec 20
            Write-Host "   /$ep -> Discord webhook `"$($r.name)`" in channel $($r.channel_id)" -ForegroundColor Green
            $ok = $true
        }
        catch { Start-Sleep -Seconds 10 }
    }
    if (-not $ok) { Write-Host "   /$ep does not answer yet - a new workers.dev address can take a few minutes to come online." -ForegroundColor Yellow }
}

Step 'Hotfix default'
$prog = Join-Path $root 'src\Program.cs'
$bytes = [IO.File]::ReadAllBytes($prog)
$bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [IO.File]::ReadAllText($prog)
$new = [regex]::Replace($text, '(public static string ReportRelay = ")[^"]*(";)', '${1}' + $url + '${2}')
if ($new -ne $text) { [IO.File]::WriteAllText($prog, $new, (New-Object Text.UTF8Encoding($bom))) }
Write-Host "   ReportRelay = $url (src\Program.cs)"

Step 'Checking with the hotfix itself (a fresh build, as every player gets it)'
. (Join-Path $root 'KHHotfix.Common.ps1')
$tmp = Join-Path $env:TEMP ('KHHotfix-relaycheck-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
try {
    Invoke-HotfixBuild $root $tmp
    Copy-Item -LiteralPath (Join-Path $root 'KHHotfix.ini') -Destination $tmp
    & (Join-Path $tmp 'KHHotfixCli.exe') --discord-test | ForEach-Object { Write-Host "   $_" }
    $checked = $LASTEXITCODE -eq 0
    if ($checked) {
        $a = Read-Host '   Send a test post to each Discord channel through the relay? [Y/n]'
        if (-not $a -or $a -match '^(y|yes|j|ja)$') { & (Join-Path $tmp 'KHHotfixCli.exe') --discord-test --send | ForEach-Object { Write-Host "   $_" } }
    }
    else { Write-Host '   The relay does not answer yet; new workers.dev addresses can take a few minutes.' -ForegroundColor Yellow }
}
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Step 'Publishing'
if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    $a = Read-Host '   Commit and push, so every download of the hotfix reports to your Discord? [Y/n]'
    if (-not $a -or $a -match '^(y|yes|j|ja)$') {
        & git -C $root add -- src/Program.cs
        & git -C $root diff --cached --quiet
        if ($LASTEXITCODE -ne 0) { & git -C $root commit -q -m "Report relay: $url" }
        & git -C $root push -q origin HEAD
        if ($LASTEXITCODE -eq 0) { Write-Host '   Pushed. New downloads send their bug and crash reports to your Discord.' -ForegroundColor Green }
        else { Write-Host '   git push failed - push the repository yourself.' -ForegroundColor Yellow }
    }
}
Write-Host ''
Write-Host "Relay ready: $url" -ForegroundColor Green
