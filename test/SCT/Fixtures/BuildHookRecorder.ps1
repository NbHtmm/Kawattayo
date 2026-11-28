param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 can compile a standalone .NET Framework console application.
if ([IO.File]::Exists($Output) -or !(Test-Path -LiteralPath (Split-Path -Parent $Output) -PathType Container)) {
    throw 'The recorder output path must be fresh and its fixture parent must exist.'
}
Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HookRecorder.cs.txt'))) -OutputAssembly $Output -OutputType ConsoleApplication
if (!(Test-Path -LiteralPath $Output -PathType Leaf)) { throw 'HookRecorder compilation produced no EXE.' }
