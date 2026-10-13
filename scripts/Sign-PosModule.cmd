@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Sign-PosModule.ps1" %*
if errorlevel 1 (
  echo No se pudo firmar el modulo. Lee el mensaje anterior.
  pause
  exit /b 1
)
echo.
pause
