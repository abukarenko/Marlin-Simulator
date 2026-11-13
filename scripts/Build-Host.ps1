param([switch]$HostOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $HostOnly) {
        $oldPath=$env:PATH
        $oldCore=$env:PLATFORMIO_CORE_DIR
        try {
            $env:PATH=(Join-Path $root '.tools\mingw64\bin')+';'+$oldPath
            $env:PLATFORMIO_CORE_DIR=Join-Path $root '.tools\platformio'
            $pio=Join-Path $env:USERPROFILE '.platformio\penv\Scripts\pio.exe'
            New-Item -ItemType Directory -Force artifacts | Out-Null
            & $pio run --project-dir vendor/Marlin-2.1.2.9 -e simulator_windows -j 4 *> artifacts/host-native-build.log
            if ($LASTEXITCODE -ne 0) { Get-Content artifacts/host-native-build.log -Tail 60; throw 'Native viewer build failed' }
        } finally { $env:PATH=$oldPath; $env:PLATFORMIO_CORE_DIR=$oldCore }
    }
    & dotnet publish src/MarlinHost.App -c Release --no-self-contained -o dist/host -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Marlin Host build failed' }
    $viewer = Join-Path $root 'vendor\Marlin-2.1.2.9\.pio\build\simulator_windows\debug\MarlinSimulator.exe'
    if (-not (Test-Path -LiteralPath $viewer)) { throw 'Build native simulator/viewer first: scripts/Build.ps1' }
    New-Item -ItemType Directory -Force dist/host/viewer | Out-Null
    Copy-Item -LiteralPath $viewer -Destination dist/host/viewer/MarlinViewer.exe
    Copy-Item .tools/mingw64/bin/*.dll dist/host/viewer/
    Copy-Item -LiteralPath LICENSE -Destination dist/host/viewer/LICENSE.txt
    New-Item -ItemType Directory -Force dist/host/viewer/licenses | Out-Null
    Copy-Item .tools/mingw64/share/licenses/* dist/host/viewer/licenses/ -Recurse -Force
    Write-Host 'Ready: dist\host\MarlinHost.App.exe'
} finally { Pop-Location }
