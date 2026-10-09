[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\local-pos'))

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\') + '\'
if (-not $packageDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El paquete debe generarse dentro de la carpeta artifacts del proyecto.'
}
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
& $dotnet publish (Join-Path $repoRoot 'src\Pos.Server\Pos.Server.csproj') --configuration Release --output $serverOutput "-p:BaseOutputPath=$serverBuildRoot\"
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del servidor POS.' }
& $dotnet publish (Join-Path $repoRoot 'src\Pos.Desktop\Pos.Desktop.csproj') --configuration Release --output $desktopOutput "-p:BaseOutputPath=$desktopBuildRoot\"
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación del punto de venta.' }

$scriptsOutput = Join-Path $packageDirectory 'scripts'
New-Item -ItemType Directory -Path $scriptsOutput -Force | Out-Null
foreach ($name in @('Pos-DatabaseSupport.ps1', 'Configure-LocalDatabase.ps1', 'Backup-LocalDatabase.ps1', 'Restore-LocalDatabase.ps1', 'Register-LocalBackupTask.ps1', 'Start-LocalPos.ps1', 'Initialize-LocalPosOperator.ps1', 'Configure-InitialOperator.ps1', 'Install-LocalPos.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scriptsOutput -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\specs\hito-5-instalacion-local-y-respaldo.md') -Destination (Join-Path $packageDirectory 'INSTRUCCIONES.md') -Force
$archivePath = Join-Path (Split-Path -Parent $packageDirectory) 'local-pos-win-x64.zip'
Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force

Write-Output "Paquete local listo en: $packageDirectory"
Write-Output "Archivo para trasladarlo a la PC de instalación: $archivePath"
