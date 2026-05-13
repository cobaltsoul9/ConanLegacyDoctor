@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "REPORT_PATH=%SCRIPT_DIR%TwilightModCheck-Results.txt"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Test-ModPakManifest.ps1" ^
  -ManifestPath "%SCRIPT_DIR%TwilightMire.Legacy.ModPakManifest.json" ^
  -ReportPath "%REPORT_PATH%"

echo.
echo Result file created:
echo %REPORT_PATH%
echo.
pause
