$ErrorActionPreference = 'Stop'
try {
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
    if (-not (Test-Path -LiteralPath $desktop -PathType Leaf)) {
        throw 'No se encontró Pos.Desktop.exe. Abre el paquete POS completo y vuelve a intentarlo.'
    }

    if ((Test-Path -LiteralPath $licenseTrustKeyRing -PathType Leaf) -and
        (Test-Path -LiteralPath $licenseTrustInstaller -PathType Leaf)) {
        $localLicenseDirectory = Join-Path $env:LOCALAPPDATA 'BalanzaPro\PosServer\Licensing'
        & $licenseTrustInstaller -PublicKeyRingPath $licenseTrustKeyRing -DestinationDirectory $localLicenseDirectory
        if (-not $?) { throw 'No se pudieron preparar las llaves públicas de licencias para el POS local.' }
    }

    # Pos.Desktop muestra primero la animación y es quien comprueba/inicia el servidor.
    Start-Process -FilePath $desktop -WorkingDirectory (Split-Path -Parent $desktop) | Out-Null
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        "No se pudo iniciar el punto de venta. $($_.Exception.Message)",
        'BalanzaPro · Inicio',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}
