@echo off
setlocal
chcp 65001 >nul
set "IMPORTER=%~dp0Import-LegacyReceivableOpeningBalances.ps1"
if not exist "%IMPORTER%" set "IMPORTER=%~dp0scripts\Import-LegacyReceivableOpeningBalances.ps1"
if not exist "%IMPORTER%" (
  echo No se encontro el importador de saldos pendientes.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%IMPORTER%" -Apply
set "RESULT=%ERRORLEVEL%"
echo.
pause
exit /b %RESULT%
