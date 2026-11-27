@echo off
setlocal
rem Resolve paths from this script, regardless of the caller's folder.
pushd "%~dp0.." || exit /b 1

set "PROJECT=avalonia\Kawattayo.Avalonia.csproj"
set "STAGE=build\single-exe-publish"
set "OUTPUT=bin-single\Kawattayo.Avalonia.exe"
if not exist "%PROJECT%" (
    echo Missing project: %CD%\%PROJECT% 1>&2
    popd
    exit /b 1
)

rem Publish the self-contained EXE and keep debug symbols out of the distribution folder.
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true ^
    -p:PublishAot=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -p:TrimMode=full ^
    -p:TrimmerRemoveSymbols=true -p:DebugType=None -p:DebugSymbols=false ^
    -p:KawattayoLocalRestore=true --source https://api.nuget.org/v3/index.json -o "%STAGE%"
if errorlevel 1 goto :failed

if not exist bin-single mkdir bin-single || goto :failed
copy /Y /B "%STAGE%\Kawattayo.Avalonia.exe" "%OUTPUT%" >nul || goto :failed
echo Executable: %CD%\%OUTPUT%
popd
exit /b 0

:failed
echo Single-file build failed. 1>&2
popd
exit /b 1
