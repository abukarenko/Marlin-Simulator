@echo off
cd /d "%~dp0"
if not exist "dist\MarlinEmulator.App.exe" (
  echo Run scripts\Build.ps1 first.
  pause
  exit /b 1
)
start "" "dist\MarlinEmulator.App.exe"
