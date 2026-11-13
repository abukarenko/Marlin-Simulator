@echo off
cd /d "%~dp0"
if not exist "dist\host\MarlinHost.App.exe" (
  echo First run scripts\Build-Host.ps1
  pause
  exit /b 1
)
start "" "dist\host\MarlinHost.App.exe"
