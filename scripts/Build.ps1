param([switch]$HostOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $HostOnly) {
        $gcc = Join-Path $root '.tools\mingw64\bin\g++.exe'
        if (-not (Test-Path -LiteralPath $gcc)) { throw 'First run: python scripts/bootstrap_native.py' }
        & python scripts/prepare_firmware.py
        if ($LASTEXITCODE -ne 0) { throw 'Firmware configuration failed' }
        $pio = Get-Command pio -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
        if (-not $pio) { $pio = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\pio.exe' }
        if (-not (Test-Path -LiteralPath $pio)) { throw 'PlatformIO Core is required: python -m pip install platformio' }
        $oldPath = $env:PATH
        $oldCore = $env:PLATFORMIO_CORE_DIR
        try {
            $env:PATH = "$(Join-Path $root '.tools\mingw64\bin');$oldPath"
            $env:PLATFORMIO_CORE_DIR = Join-Path $root '.tools\platformio'
            & $pio run --project-dir vendor/Marlin-2.1.2.9 -e simulator_windows -j 4
            if ($LASTEXITCODE -ne 0) { throw 'Marlin build failed' }
        } finally { $env:PATH = $oldPath; $env:PLATFORMIO_CORE_DIR = $oldCore }
    }
    & dotnet publish src/MarlinEmulator.App -c Release --no-self-contained -o dist -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Windows application build failed' }
    $binary = Join-Path $root 'vendor\Marlin-2.1.2.9\.pio\build\simulator_windows\debug\MarlinSimulator.exe'
    if (Test-Path -LiteralPath $binary) {
        New-Item -ItemType Directory -Force dist/firmware | Out-Null
        Copy-Item -LiteralPath $binary -Destination dist/firmware/
        Copy-Item .tools/mingw64/bin/*.dll dist/firmware/
        Copy-Item vendor/Marlin-2.1.2.9/LICENSE dist/firmware/Marlin-LICENSE.txt
        New-Item -ItemType Directory -Force dist/firmware/licenses | Out-Null
        Copy-Item .tools/mingw64/share/licenses/* dist/firmware/licenses/ -Recurse -Force
    }
    Write-Host 'Ready: dist\MarlinEmulator.App.exe'
} finally { Pop-Location }
