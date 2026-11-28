@echo off
setlocal
if not "%~2"=="" goto :usage
if "%~1"=="" goto :start
if /i "%~1"=="unit" goto :start
if /i "%~1"=="integration" goto :start
goto :usage

:start
pushd "%~dp0.." || exit /b 1
if /i "%~1"=="integration" goto :integration
call test\run_ut.bat
if errorlevel 1 goto :failed
if /i "%~1"=="unit" goto :success

:integration
dotnet build avalonia/Kawattayo.Avalonia.csproj -c Release -p:KawattayoLocalRestore=true -p:KawattayoSmoke=true -p:UseSharedCompilation=false -m:1 -o build/smoke-bin
if errorlevel 1 goto :failed
powershell.exe -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; $id=[Guid]::NewGuid().ToString('N'); $fixture=[IO.Path]::GetFullPath('build/smoke-'+$id); [void][IO.Directory]::CreateDirectory($fixture); $report=Join-Path $fixture 'result.txt'; $exe=[IO.Path]::GetFullPath('build/smoke-bin/Kawattayo.Avalonia.exe'); $quote=[char]34; $argsLine='--smoke '+$quote+$fixture+$quote+' '+$quote+$report+$quote; $p=Start-Process -FilePath $exe -ArgumentList $argsLine -WindowStyle Hidden -PassThru; if (!$p.WaitForExit(90000)) { $p.Kill(); throw 'GUI test timed out.' }; $result=Get-Content -LiteralPath $report -Encoding UTF8 -Raw; Write-Output $result; if ($p.ExitCode -ne 0 -or !$result.StartsWith('PASS:')) {exit 1}"
if errorlevel 1 goto :failed

:success
popd
exit /b 0

:failed
popd
exit /b 1

:usage
echo Usage: %~nx0 [unit^|integration] 1>&2
exit /b 2
