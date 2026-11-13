param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root "artifacts/release-$Version"
if (Test-Path -LiteralPath $package) { throw "Package folder already exists: $package" }
$hostDir = Join-Path $package 'dist'
New-Item -ItemType Directory -Force -Path $hostDir | Out-Null
& dotnet publish (Join-Path $root 'src/MarlinEmulator.App') -c Release --no-self-contained -o $hostDir -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Host build failed' }
$firmware = Join-Path $hostDir 'firmware'
New-Item -ItemType Directory -Force -Path $firmware | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'vendor/Marlin-2.1.2.9/.pio/build/simulator_windows/debug/MarlinSimulator.exe') -Destination $firmware
& (Join-Path $root '.tools/mingw64/bin/strip.exe') --strip-debug (Join-Path $firmware 'MarlinSimulator.exe')
if ($LASTEXITCODE -ne 0) { throw 'Strip failed' }
foreach($dll in @('libgcc_s_seh-1.dll','libwinpthread-1.dll','libstdc++-6.dll','SDL2.dll','SDL2_net.dll')) {
    Copy-Item -LiteralPath (Join-Path $root ".tools/mingw64/bin/$dll") -Destination $firmware
}
Copy-Item -LiteralPath (Join-Path $root '.tools/mingw64/share/licenses') -Destination (Join-Path $firmware 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'vendor/Marlin-2.1.2.9/LICENSE') -Destination (Join-Path $firmware 'Marlin-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'vendor/MarlinSimUI-29c11d4f63/LICENSE') -Destination (Join-Path $firmware 'MarlinSimUI-LICENSE.txt')
foreach($name in @('Start.cmd','README.md','LICENSE','SOURCES.json','assets','examples')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package -Recurse
}
$archive = Join-Path $root "artifacts/Marlin-Simulator-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive
(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLower() + '  ' + (Split-Path $archive -Leaf) | Set-Content -Path "$archive.sha256" -Encoding ascii
Write-Host $archive
