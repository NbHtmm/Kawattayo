# Windows x64 用の Native AOT 配布 ZIP。利用者データや PDB は入れない。
param(
    [string]$PublishDirectory,
    [string]$AssetsFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (!$PublishDirectory) {
        $PublishDirectory = Join-Path $root ('build/avalonia-publish-' + [Guid]::NewGuid().ToString('N'))
        dotnet publish avalonia/Kawattayo.Avalonia.csproj -c Release -r win-x64 -p:PublishAot=true -p:OptimizationPreference=Size -p:DebugType=None -p:DebugSymbols=false -p:RestoreLockedMode=true --source https://api.nuget.org/v3/index.json -o $PublishDirectory
        if ($LASTEXITCODE) { throw 'Native AOT publish failed.' }
    }
    if (!$AssetsFile) { $AssetsFile = 'avalonia/obj/project.assets.json' }
    $assets = Get-Content -LiteralPath $AssetsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    function Find-PackageFile([string]$id, [string]$name, [string]$version = '') {
        if (!$version) {
            $keys = @($assets.libraries.PSObject.Properties.Name | Where-Object { ($_ -split '/')[0] -eq $id })
            if ($keys.Count -ne 1) { throw "Could not identify resolved package: $id" }
            $version = ($keys[0] -split '/')[1]
        }
        foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
            $path = Join-Path $cache ($id.ToLowerInvariant() + '/' + $version + '/' + $name)
            if (Test-Path -LiteralPath $path -PathType Leaf) { return $path }
        }
        throw "Package document not found: $id/$version/$name"
    }
    $netLicense = Join-Path $root 'avalonia/DotNet-LICENSE.txt'
    $entries = [ordered]@{
        'かわったよ.exe' = Join-Path $PublishDirectory 'Kawattayo.Avalonia.exe'
        'av_libglesv2.dll' = Join-Path $PublishDirectory 'av_libglesv2.dll'
        'libHarfBuzzSharp.dll' = Join-Path $PublishDirectory 'libHarfBuzzSharp.dll'
        'libSkiaSharp.dll' = Join-Path $PublishDirectory 'libSkiaSharp.dll'
        '使い方.md' = Join-Path $root 'avalonia/README.md'
        'LICENSE.txt' = Join-Path $root 'LICENSE'
        'licenses/Avalonia-LICENSE.txt' = Join-Path $root 'avalonia/Avalonia-LICENSE.txt'
        'licenses/dotnet-LICENSE.txt' = $netLicense
        'licenses/dotnet-AOT-NOTICES.txt' = Find-PackageFile 'runtime.win-x64.Microsoft.DotNet.ILCompiler' 'THIRD-PARTY-NOTICES.TXT'
        'licenses/ANGLE-LICENSE.txt' = Find-PackageFile 'Avalonia.Angle.Windows.Natives' 'LICENSE'
        'licenses/SkiaSharp-LICENSE.txt' = Find-PackageFile 'SkiaSharp' 'LICENSE.txt'
        'licenses/SkiaSharp-NOTICES.txt' = Find-PackageFile 'SkiaSharp.NativeAssets.Win32' 'THIRD-PARTY-NOTICES.txt'
        'licenses/HarfBuzzSharp-LICENSE.txt' = Find-PackageFile 'HarfBuzzSharp' 'LICENSE.txt'
        'licenses/HarfBuzzSharp-NOTICES.txt' = Find-PackageFile 'HarfBuzzSharp.NativeAssets.Win32' 'THIRD-PARTY-NOTICES.txt'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $output = Join-Path $root 'build/Kawattayo_v0.2.2_Avalonia_Windows_x64.zip'
    $temporary = $output + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $entries.GetEnumerator()) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key,
                    [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $archive.Dispose() }
        if ([IO.File]::Exists($output)) { [IO.File]::Replace($temporary, $output, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $output) }
        Get-Item -LiteralPath $output
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
} finally { Pop-Location }
