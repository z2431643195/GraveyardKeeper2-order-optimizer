param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Graveyard Keeper 2"
)

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$outDir = Join-Path $root "release"
$stage = Join-Path $outDir "stage"
$pluginDir = Join-Path $stage "BepInEx\plugins"
$project = Join-Path $root "OrderOptimizer.csproj"

Write-Host "=== Building Release ===" -ForegroundColor Cyan
& dotnet build $project -c Release -v minimal "-p:GameDir=$GameDir" "-p:PluginsDir=NoCopy"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

$dll = Join-Path $root "bin\Release\net472\OrderOptimizer.dll"
Copy-Item $dll $pluginDir -Force
Copy-Item (Join-Path $root "README.txt") -Destination $stage -Force
Copy-Item (Join-Path $root "CHANGELOG.txt") -Destination $stage -Force
Copy-Item (Join-Path $root "LICENSE.txt") -Destination $stage -Force

$ver = (Get-Item $dll).VersionInfo.FileVersion
$zip = Join-Path $outDir "OrderOptimizer-v$ver.zip"
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force

Write-Host ""
Write-Host "=== Package created: $zip ===" -ForegroundColor Green
Write-Host "=== Contents ===" -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
$archive.Entries | ForEach-Object { Write-Host ("  {0}  ({1} bytes)" -f $_.FullName, $_.Length) }
$archive.Dispose()
