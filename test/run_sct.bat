@echo off
setlocal
if not "%~2"=="" exit /b 2
set "KAWATTAYO_SCT_PACKAGE="
if not "%~1"=="" set "KAWATTAYO_SCT_PACKAGE=%~f1"
pushd "%~dp0.." || exit /b 1
set "SCT_POWERSHELL=powershell.exe"
where pwsh.exe >nul 2>nul && set "SCT_POWERSHELL=pwsh.exe"
"%SCT_POWERSHELL%" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File test\SCT\run.ps1
set "status=%ERRORLEVEL%"
popd
exit /b %status%
