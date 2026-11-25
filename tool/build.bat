@echo off
setlocal
chcp 65001 >nul
pushd "%~dp0.." || exit /b 1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=true
where dotnet.exe >nul 2>&1 || goto :missing
dotnet publish "src\ShareFileWatch.csproj" -c Release -r win-x64 --self-contained true --configfile NuGet.Config --artifacts-path "build\artifacts" -p:NuGetAudit=false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:SatelliteResourceLanguages=ja -p:DebugType=None -p:DebugSymbols=false -o "build\publish" || goto :failed
if not exist bin mkdir bin
copy /y /b "build\publish\ShareFileWatch.exe" "bin\かわったよ.exe" >nul || goto :failed
echo Distribution: %CD%\bin\かわったよ.exe
popd
exit /b 0
:missing
echo Install the .NET SDK on this development PC. Nothing was downloaded.
:failed
popd
exit /b 1