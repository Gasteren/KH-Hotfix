@echo off
rem Builds KHHotfix.exe (Steam wrapper, no console) and KHHotfixCli.exe (maintenance commands)
rem with the C# compiler that ships with the .NET Framework 4.x on every Windows 10/11 PC.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4.x C# compiler not found: %CSC%
  exit /b 1
)
set "OPTS=/nologo /optimize+ /platform:x64 /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll"
"%CSC%" %OPTS% /target:winexe /out:KHHotfix.exe src\*.cs || exit /b 1
"%CSC%" %OPTS% /target:exe /define:CLI /out:KHHotfixCli.exe src\*.cs || exit /b 1
echo Build OK: KHHotfix.exe, KHHotfixCli.exe
