[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\local-pos'),
    [string]$ArchivePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\local-pos-win-x64.zip')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $packageDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El paquete debe generarse dentro de la carpeta artifacts del proyecto.'
}
$archiveFullPath = [IO.Path]::GetFullPath($ArchivePath)
if (-not $archiveFullPath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetExtension($archiveFullPath) -ine '.zip' -or
    $archiveFullPath.StartsWith($packageDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El archivo ZIP debe guardarse en artifacts, fuera de la carpeta del paquete.'
}
$archiveParent = Split-Path -Parent $archiveFullPath
New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
$serverOutput = Join-Path $packageDirectory 'server'
$desktopOutput = Join-Path $packageDirectory 'desktop'
$buildRoot = "$($packageDirectory.TrimEnd('\'))-build"
$serverBuildRoot = Join-Path $buildRoot 'server'
$desktopBuildRoot = Join-Path $buildRoot 'desktop'

$resolvedCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -First 1
$dotnet = if ($resolvedCommand) { $resolvedCommand.Source } else { $null }
if (-not $dotnet -and (Test-Path 'C:\Program Files\dotnet\dotnet.exe')) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not $dotnet) { throw 'No se encontró el SDK de .NET para preparar el paquete.' }

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
foreach ($packagePart in @($serverOutput, $desktopOutput, (Join-Path $packageDirectory 'scripts'), $buildRoot)) {
    if (Test-Path -LiteralPath $packagePart) {
        $resolvedPart = [IO.Path]::GetFullPath($packagePart)
        $safeToClean = if ($resolvedPart -ieq [IO.Path]::GetFullPath($buildRoot)) {
            $resolvedPart.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)
        } else {
            $resolvedPart.StartsWith($packageDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
        }
        if (-not $safeToClean) {
            throw 'Se rechazó limpiar una ruta que está fuera de la carpeta del paquete.'
        }
        Remove-Item -LiteralPath $resolvedPart -Recurse -Force
    }
}
& $dotnet publish (Join-Path $repoRoot 'src\Pos.Server\Pos.Server.csproj') --configuration Release --runtime win-x64 --self-contained true --output $serverOutput "-p:BaseOutputPath=$serverBuildRoot\"
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del servidor POS.' }
& $dotnet publish (Join-Path $repoRoot 'src\Pos.Desktop\Pos.Desktop.csproj') --configuration Release --runtime win-x64 --self-contained true --output $desktopOutput "-p:BaseOutputPath=$desktopBuildRoot\"
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del punto de venta.' }

function Remove-PublishedBuildArtifacts {
    param([Parameter(Mandatory)][string]$PublishRoot)
    $resolvedRoot = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($packageDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Se rechazó limpiar contenido fuera de la carpeta publicada del POS.'
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $PublishRoot -Directory -Recurse -Force | Where-Object Name -eq 'bin' | Sort-Object FullName)) {
        $fullDirectory = [IO.Path]::GetFullPath($directory.FullName)
        if ($fullDirectory.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $fullDirectory)) {
            Remove-Item -LiteralPath $fullDirectory -Recurse -Force
        }
    }
    foreach ($developmentSettings in @(Get-ChildItem -LiteralPath $PublishRoot -Filter 'appsettings.Development.json' -File -Recurse -Force)) {
        $fullFile = [IO.Path]::GetFullPath($developmentSettings.FullName)
        if ($fullFile.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $fullFile -Force
        }
    }
}
Remove-PublishedBuildArtifacts -PublishRoot $serverOutput
Remove-PublishedBuildArtifacts -PublishRoot $desktopOutput

$scriptsOutput = Join-Path $packageDirectory 'scripts'
New-Item -ItemType Directory -Path $scriptsOutput -Force | Out-Null
foreach ($name in @('Pos-DatabaseSupport.ps1', 'Configure-LocalDatabase.ps1', 'Backup-LocalDatabase.ps1', 'Restore-LocalDatabase.ps1', 'Register-LocalBackupTask.ps1', 'Start-LocalPos.ps1', 'Stop-ProcessByPort.ps1', 'Initialize-LocalPosOperator.ps1', 'Configure-InitialOperator.ps1', 'Install-LocalPos.ps1', 'Install-WarehouseModuleTrust.ps1', 'Manage-PosWindowsService.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scriptsOutput -Force
}
function Write-WindowsCommandFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Content)
    $windowsContent = $Content -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText($Path, $windowsContent, [Text.Encoding]::ASCII)
}
$launcherPath = Join-Path $packageDirectory 'Iniciar POS.cmd'
$launcherContent = @'
@echo off
setlocal
set "STARTER=%~dp0scripts\Start-LocalPos.ps1"
if not exist "%STARTER%" (
  echo No se encontro la aplicacion del punto de venta.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%STARTER%"
if errorlevel 1 (
  echo No se pudo iniciar el punto de venta. Revisa el mensaje anterior.
  pause
  exit /b 1
)
'@
Write-WindowsCommandFile -Path $launcherPath -Content $launcherContent
$stopperPath = Join-Path $packageDirectory 'Detener proceso por puerto.cmd'
$stopperContent = @'
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
'@
Write-WindowsCommandFile -Path $stopperPath -Content $stopperContent
$updateLauncherPath = Join-Path $packageDirectory 'Actualizar POS.cmd'
$updateLauncherContent = @'
@echo off
setlocal
set "UPDATER=%~dp0scripts\Install-LocalPos.ps1"
if not exist "%UPDATER%" (
  echo No se encontro el actualizador del punto de venta.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%UPDATER%" -PackageDirectory "%~dp0."
if errorlevel 1 (
  echo No se completo la actualizacion. Lee el mensaje anterior y vuelve a intentarlo.
  pause
  exit /b 1
)
'@
Write-WindowsCommandFile -Path $updateLauncherPath -Content $updateLauncherContent
$serviceLauncherPath = Join-Path $packageDirectory 'Administrar servicio POS.cmd'
$serviceLauncherContent = @'
@echo off
setlocal
set "MANAGER=%~dp0scripts\Manage-PosWindowsService.ps1"
if not exist "%MANAGER%" (
  echo No se encontro la herramienta de administracion del servicio POS.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MANAGER%"
if errorlevel 1 (
  echo No se pudo completar la operacion del servicio. Lee el mensaje anterior.
  pause
  exit /b 1
)
'@
Write-WindowsCommandFile -Path $serviceLauncherPath -Content $serviceLauncherContent
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\manuales\INSTRUCCIONES-INSTALACION-POS.md') -Destination (Join-Path $packageDirectory 'INSTRUCCIONES.md') -Force
$legacyModesGuide = Join-Path $packageDirectory 'MODOS DE INSTALACION.md'
if (Test-Path -LiteralPath $legacyModesGuide) { Remove-Item -LiteralPath $legacyModesGuide -Force }
Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archiveFullPath -CompressionLevel Optimal -Force

Write-Output "Paquete local listo en: $packageDirectory"
Write-Output "Archivo para trasladarlo a la PC de instalación: $archiveFullPath"
