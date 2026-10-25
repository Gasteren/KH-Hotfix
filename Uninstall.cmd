@echo off
if not exist "%~dp0Uninstall-KHHotfix.ps1" (
  echo.
  echo   This file was started from inside the ZIP file.
  echo   Right-click the ZIP file, choose Extract All, then run Uninstall.cmd from the extracted folder.
  echo.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-KHHotfix.ps1" %*
echo.
pause
