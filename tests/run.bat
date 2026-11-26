@echo off
setlocal
pushd "%~dp0.." || exit /b 1
if not exist build\tests mkdir build\tests || goto :failed
dotnet build tests\Tests.csproj -c Release --configfile NuGet.Config -p:NuGetAudit=false -o build\tests\runner || goto :failed
build\tests\runner\Tests.exe --self-test build\tests\self.txt || goto :failed
build\tests\runner\Tests.exe --ui-test build\tests\preview-ui.txt || goto :failed
popd
exit /b 0
:failed
popd
exit /b 1