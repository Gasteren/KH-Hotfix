@echo off
rem Deploys the KH Hotfix report relay to your Cloudflare account (see Deploy-Relay.ps1).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-Relay.ps1" %*
echo.
pause
