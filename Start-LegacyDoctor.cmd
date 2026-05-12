@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0LegacyDoctor.Gui.ps1"
endlocal
