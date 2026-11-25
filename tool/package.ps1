Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 任意の発行フォルダを丸ごと圧縮せず、下の配布ファイル一覧だけを収録する。
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.bat')
if ($LASTEXITCODE -ne 0) { throw 'Build failed. The previous ZIP was not changed.' }

$assetsPath = Join-Path $root 'build\artifacts\obj\ShareFileWatch\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$assetsFramework = @($assets.project.frameworks.PSObject.Properties.Value)
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)

function Resolve-PackFile([string]$id, [string]$name) {
    # 最新版を推測せず、今回の復元で選ばれたランタイムパックの文書を採用する。
    $pack = @($assetsFramework | ForEach-Object { $_.downloadDependencies } |
        Where-Object { $_.name -eq $id })
    if ($pack.Count -ne 1 -or $pack[0].version -notmatch '^\[([^, \]]+),\s*\1\]$') {
        throw "Exact runtime pack version not found: $id"
    }
    $packagePath = Join-Path $id.ToLowerInvariant() $Matches[1].ToLowerInvariant()
    foreach ($cache in $packageRoots) {
        $candidate = Join-Path (Join-Path $cache $packagePath) $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "Runtime pack document not found: $id/$name"
}

$core = 'Microsoft.NETCore.App.Runtime.win-x64'
$desktop = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'
$entries = [ordered]@{
    'かわったよ.exe' = Join-Path $root 'build\publish\ShareFileWatch.exe'
    '使い方.txt' = Join-Path $root '使い方.txt'
    'LICENSE.txt' = Join-Path $root 'LICENSE'
    'licenses/dotnet-runtime-LICENSE.txt' = Resolve-PackFile $core 'LICENSE.TXT'
    'licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt' = Resolve-PackFile $core 'THIRD-PARTY-NOTICES.TXT'
    'licenses/dotnet-windowsdesktop-LICENSE.txt' = Resolve-PackFile $desktop 'LICENSE'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$target = Join-Path $root 'build\Kawattayo_win_x64.zip'
$temporary = "$target.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $entry.Value, $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $zip.Dispose()
    }
    if ([IO.File]::Exists($target)) {
        [IO.File]::Replace($temporary, $target, [NullString]::Value)
    } else {
        [IO.File]::Move($temporary, $target)
    }
    Write-Output $target
} finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
