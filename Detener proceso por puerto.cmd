@echo off
setlocal EnableDelayedExpansion
net session >nul 2>&1
if errorlevel 1 (
  echo Solicitando permisos de administrador para detener servicios o procesos protegidos...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b !ERRORLEVEL!
)
set "STOPPER=%~dp0scripts\Stop-ProcessByPort.ps1"
if not exist "%STOPPER%" (
  echo No se encontro scripts\Stop-ProcessByPort.ps1 junto a este archivo.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%STOPPER%"
set "RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %RESULT%
