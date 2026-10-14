[CmdletBinding()]
param([string]$InstallerPath)

$ErrorActionPreference = 'Stop'
$installer = if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    Join-Path $PSScriptRoot '..\..\scripts\Install-ModulePublisherTrust.ps1'
} else {
    $InstallerPath
}
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'RED: todavía no existe la instalación aditiva de confianza para el nuevo publicador.'
}

$root = Join-Path ([IO.Path]::GetTempPath()) ("pos-trust-test-" + [guid]::NewGuid().ToString('N'))
$previousLocalAppData = $env:LOCALAPPDATA
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $env:LOCALAPPDATA = $root
    $securityDirectory = Join-Path $root 'PuntoDeVenta\Security'
    New-Item -ItemType Directory -Path $securityDirectory -Force | Out-Null
    $ringPath = Join-Path $securityDirectory 'TrustedModuleKeys.json'
    $oldKey = [Security.Cryptography.RSA]::Create(2048)
    $newKey = [Security.Cryptography.RSA]::Create(2048)
    $otherKey = [Security.Cryptography.RSA]::Create(2048)
    try {
        $oldPem = $oldKey.ExportSubjectPublicKeyInfoPem()
        $newPem = $newKey.ExportSubjectPublicKeyInfoPem()
        $otherPem = $otherKey.ExportSubjectPublicKeyInfoPem()
        [IO.File]::WriteAllText((Join-Path $root 'new.pem'), $newPem, [Text.Encoding]::ASCII)
        [IO.File]::WriteAllText((Join-Path $root 'other.pem'), $otherPem, [Text.Encoding]::ASCII)
        $initialRing = [pscustomobject]@{
            schemaVersion = 1
            keys = @([pscustomobject]@{ keyId = 'legacy-publisher'; publicKeyPem = $oldPem; status = 'Active' })
        } | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText($ringPath, $initialRing, [Text.UTF8Encoding]::new($false))

        & $installer -PublicKeyPath (Join-Path $root 'new.pem') -KeyId 'pos-publisher-vps-2026'
        $ring = Get-Content -LiteralPath $ringPath -Raw | ConvertFrom-Json
        if (@($ring.keys).Count -ne 2) { throw 'La clave nueva debe agregarse sin quitar la clave anterior.' }
        if (($ring.keys | Where-Object keyId -eq 'legacy-publisher').publicKeyPem -cne $oldPem) {
            throw 'La clave anterior se modificó durante la incorporación.'
        }
        if (($ring.keys | Where-Object keyId -eq 'pos-publisher-vps-2026').publicKeyPem -cne $newPem) {
            throw 'No se agregó la clave pública esperada.'
        }

        $collisionRejected = $false
        try { & $installer -PublicKeyPath (Join-Path $root 'other.pem') -KeyId 'pos-publisher-vps-2026' }
        catch { $collisionRejected = $_.Exception.Message.Contains('ya existe', [StringComparison]::OrdinalIgnoreCase) }
        if (-not $collisionRejected) { throw 'Un ID existente asociado a otra clave debe rechazarse.' }
        $afterCollision = Get-Content -LiteralPath $ringPath -Raw | ConvertFrom-Json
        if (@($afterCollision.keys).Count -ne 2 -or
            ($afterCollision.keys | Where-Object keyId -eq 'pos-publisher-vps-2026').publicKeyPem -cne $newPem) {
            throw 'El anillo cambió después de rechazar una colisión.'
        }
    } finally {
        $oldKey.Dispose()
        $newKey.Dispose()
        $otherKey.Dispose()
    }
} finally {
    $env:LOCALAPPDATA = $previousLocalAppData
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output 'OK: la clave nueva se agrega sin quitar otras y las colisiones se rechazan.'
