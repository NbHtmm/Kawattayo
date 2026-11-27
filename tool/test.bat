@echo off
setlocal
pushd "%~dp0.." || exit /b 1
dotnet run --project tests/Core.Tests/Kawattayo.Core.Tests.csproj -c Release -p:UseSharedCompilation=false
if errorlevel 1 goto :failed
dotnet build avalonia/Kawattayo.Avalonia.csproj -c Release -p:KawattayoLocalRestore=true -p:KawattayoSmoke=true -p:UseSharedCompilation=false -m:1 -o build/smoke-bin
if errorlevel 1 goto :failed
powershell.exe -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; $id=[Guid]::NewGuid().ToString('N'); $fixture=[IO.Path]::GetFullPath('build/smoke-'+$id); [void][IO.Directory]::CreateDirectory($fixture); $report=Join-Path $fixture 'result.txt'; $exe=[IO.Path]::GetFullPath('build/smoke-bin/Kawattayo.Avalonia.exe'); $quote=[char]34; $argsLine='--smoke '+$quote+$fixture+$quote+' '+$quote+$report+$quote; $p=Start-Process -FilePath $exe -ArgumentList $argsLine -WindowStyle Hidden -PassThru; if (!$p.WaitForExit(90000)) { $p.Kill(); throw 'GUI test timed out.' }; $result=Get-Content -LiteralPath $report -Encoding UTF8 -Raw; Write-Output $result; if ($p.ExitCode -ne 0 -or !$result.StartsWith('PASS:')) {exit 1}"
if errorlevel 1 goto :failed
popd
exit /b 0

:failed
popd
exit /b 1
