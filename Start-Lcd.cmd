@echo off
if not exist "%~dp0dist\lcd\EasyMarlinEmulator.exe" (
  echo Run scripts\Build-Lcd.ps1 first.
  pause
  exit /b 1
)
cd /d "%~dp0dist\lcd"
start "" EasyMarlinEmulator.exe %*
