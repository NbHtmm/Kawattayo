@echo off
setlocal
pushd "%~dp0.." || exit /b 1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File test\UT\run.ps1
set "status=%ERRORLEVEL%"
popd
exit /b %status%
