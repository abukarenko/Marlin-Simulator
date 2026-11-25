param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root "artifacts/Easy-Marlin-Emulator-$Version-win-x64"
if (Test-Path -LiteralPath $package) { throw "Package folder already exists: $package" }
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'dist/lcd/EasyMarlinEmulator.exe') -Destination $package
& (Join-Path $root '.tools/mingw64/bin/strip.exe') --strip-debug (Join-Path $package 'EasyMarlinEmulator.exe')
if ($LASTEXITCODE -ne 0) { throw 'Strip failed' }
foreach ($dll in @('libgcc_s_seh-1.dll', 'libwinpthread-1.dll', 'libstdc++-6.dll', 'libdl.dll', 'SDL2.dll', 'SDL2_net.dll')) {
    Copy-Item -LiteralPath (Join-Path $root ".tools/mingw64/bin/$dll") -Destination $package
}
Copy-Item -LiteralPath (Join-Path $root '.tools/mingw64/share/licenses') -Destination (Join-Path $package 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'vendor/Marlin-2.1.2.9/.pio/libdeps/lcd_windows/U8glib-HAL/LICENSE') -Destination (Join-Path $package 'licenses/U8glib-HAL-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'vendor/MarlinSimUI-29c11d4f63/LICENSE') -Destination (Join-Path $package 'licenses/MarlinSimUI-LICENSE.txt')
$thirdParty = Join-Path $root 'vendor/MarlinSimUI-29c11d4f63/vendor'
Get-ChildItem -LiteralPath $thirdParty -Recurse -File -Filter '*LICENSE*' | ForEach-Object {
    $relative = $_.FullName.Substring($thirdParty.Length + 1)
    $destination = Join-Path $package "licenses/MarlinSimUI-vendor/$relative"
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $package 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'SOURCES.json') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'LCD.md') -Destination (Join-Path $package 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'examples') -Destination $package -Recurse
@'
@echo off
cd /d "%~dp0"
start "" EasyMarlinEmulator.exe %*
'@ | Set-Content -LiteralPath (Join-Path $package 'Start-Easy-Marlin-Emulator.cmd') -Encoding ascii
$archive = "$package.zip"
Compress-Archive -LiteralPath $package -DestinationPath $archive
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLower() + '  ' + (Split-Path $archive -Leaf) | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Host $archive
