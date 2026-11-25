param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & python scripts/prepare_firmware.py
    if ($LASTEXITCODE -ne 0) { throw 'Firmware preparation failed' }
    $pio = Get-Command pio -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
    if (-not $pio) { $pio = Join-Path $env:USERPROFILE '.platformio\penv\Scripts\pio.exe' }
    $oldPath = $env:PATH
    $oldCore = $env:PLATFORMIO_CORE_DIR
    try {
        $env:PATH = (Join-Path $root '.tools\mingw64\bin') + ';' + $oldPath
        $env:PLATFORMIO_CORE_DIR = Join-Path $root '.tools\platformio'
        New-Item -ItemType Directory -Force artifacts | Out-Null
        # Windows PowerShell treats harmless native stderr warnings as errors.
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & $pio run --project-dir vendor/Marlin-2.1.2.9 -e lcd_windows -j 4 *> artifacts/lcd-build.log
        $buildExitCode = $LASTEXITCODE
        $ErrorActionPreference = $oldPreference
        if ($buildExitCode -ne 0) { Get-Content artifacts/lcd-build.log -Tail 65; throw 'LCD firmware build failed' }
    } finally { $env:PATH = $oldPath; $env:PLATFORMIO_CORE_DIR = $oldCore }
    New-Item -ItemType Directory -Force dist/lcd | Out-Null
    Copy-Item vendor/Marlin-2.1.2.9/.pio/build/lcd_windows/debug/MarlinSimulator.exe dist/lcd/EasyMarlinEmulator.exe
    Copy-Item .tools/mingw64/bin/*.dll dist/lcd/
    Copy-Item LICENSE dist/lcd/LICENSE.txt
    New-Item -ItemType Directory -Force dist/lcd/licenses | Out-Null
    Copy-Item .tools/mingw64/share/licenses/* dist/lcd/licenses/ -Recurse -Force
    Copy-Item LCD.md dist/lcd/
    Write-Host 'Ready: dist\lcd\EasyMarlinEmulator.exe'
} finally { Pop-Location }
