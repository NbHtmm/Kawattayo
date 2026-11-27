@echo off
setlocal
pushd "%~dp0.." || exit /b 1
rem Build the portable Windows executable directly; no PowerShell script is needed.
dotnet publish "avalonia\Kawattayo.Avalonia.csproj" -c Release -r win-x64 -p:PublishAot=true -p:OptimizationPreference=Size -p:DebugType=None -p:DebugSymbols=false -p:KawattayoLocalRestore=true -p:KawattayoCopyToBin=true --source https://api.nuget.org/v3/index.json -o "build\avalonia-publish"
set "BUILD_EXIT=%ERRORLEVEL%"
popd
exit /b %BUILD_EXIT%
