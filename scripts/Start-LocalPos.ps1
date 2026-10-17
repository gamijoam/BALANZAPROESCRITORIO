$ErrorActionPreference = 'Stop'
$support = Join-Path $PSScriptRoot 'Pos-DatabaseSupport.ps1'
if (Test-Path -LiteralPath $support -PathType Leaf) {
    . $support
    try { $env:POS_POSTGRES_BIN = Get-PostgreSqlBinaryDirectory }
    catch { Remove-Item Env:POS_POSTGRES_BIN -ErrorAction SilentlyContinue }
}
$installRoot = Split-Path -Parent $PSScriptRoot
$desktop = Join-Path $installRoot 'desktop\Pos.Desktop.exe'
$licenseTrustKeyRing = Join-Path $installRoot 'trusted-license-keys.json'
$licenseTrustInstaller = Join-Path $PSScriptRoot 'Install-PosLicenseTrustKeys.ps1'
$serviceManager = Join-Path $PSScriptRoot 'Manage-PosWindowsService.ps1'
if (-not (Test-Path -LiteralPath $desktop -PathType Leaf)) {
    throw 'No se encontró Pos.Desktop.exe. Abre el paquete POS completo y vuelve a intentarlo.'
}

if ((Test-Path -LiteralPath $licenseTrustKeyRing -PathType Leaf) -and
    (Test-Path -LiteralPath $licenseTrustInstaller -PathType Leaf)) {
    $localLicenseDirectory = Join-Path $env:LOCALAPPDATA 'BalanzaPro\PosServer\Licensing'
    & $licenseTrustInstaller -PublicKeyRingPath $licenseTrustKeyRing -DestinationDirectory $localLicenseDirectory
    if (-not $?) { throw 'No se pudieron preparar las llaves públicas de licencias para el POS local.' }
}

if (Test-Path -LiteralPath $serviceManager -PathType Leaf) {
    & $serviceManager -Action EnsureRunning
}

# El escritorio comprueba o inicia el servidor local antes de mostrar el login.
Start-Process -FilePath $desktop -WorkingDirectory (Split-Path -Parent $desktop) | Out-Null
