@echo off
setlocal
powershell.exe -NoLogo -NoProfile -NonInteractive -File "%~dp0package-avalonia.ps1" -CopyToBin
exit /b %ERRORLEVEL%