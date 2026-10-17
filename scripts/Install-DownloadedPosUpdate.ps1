[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][int]$DesktopProcessId
)

$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$updatesRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'BalanzaPro\Updates')).TrimEnd('\') + '\'
if (-not $package.StartsWith($updatesRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'El paquete temporal está fuera de la carpeta de actualizaciones del POS.'
}
$installer = Join-Path $package 'scripts\Install-LocalPos.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'El paquete descargado no contiene el instalador del POS.'
}

try {
    $desktopProcess = Get-Process -Id $DesktopProcessId -ErrorAction SilentlyContinue
    if ($null -ne $desktopProcess) {
        Write-Output 'Esperando que cierre el punto de venta…'
        $desktopProcess.WaitForExit(120000) | Out-Null
        if (-not $desktopProcess.HasExited) {
            throw 'El POS sigue abierto. Cierra las ventanas del programa y vuelve a intentar la actualización.'
        }
    }
    & $installer -PackageDirectory $package
    if (-not $?) { throw 'El instalador no completó la actualización.' }
    $downloadDirectory = Split-Path -Parent $package
    if ([IO.Path]::GetFileName($package) -ieq 'package' -and
        [IO.Path]::GetFileName($downloadDirectory) -match '^[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $downloadDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Output 'La actualización terminó y se limpió la descarga temporal.'
} catch {
    [Console]::Error.WriteLine("No se pudo actualizar el POS: $($_.Exception.Message)")
    Write-Output "El paquete temporal sigue disponible en '$package' para revisar el problema."
    Read-Host 'Presiona Enter para cerrar esta ventana' | Out-Null
    exit 1
}
