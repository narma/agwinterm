@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-gui.ps1" %*
if errorlevel 1 (
  echo.
  echo GUI update failed. See the message above.
  pause
  exit /b 1
)
pause
